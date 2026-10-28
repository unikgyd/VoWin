using VoSharp.Telephony.Calls;
using VoSharp.Telephony.VoWifi;
using System.Collections.Concurrent;
using VoSharp.Telephony.Sms;

namespace VoSharp.Kernel.SipGateway;

public sealed record SipGatewayCarrierCall(CallInfo Call, string? SlotId, ICallPcmMedia Media);

public interface ISipGatewayCallController
{
    event EventHandler<IncomingCallEventArgs>? IncomingCall;
    event EventHandler<CallEndedEventArgs>? CallEnded;

    Task<SipGatewayCarrierCall> DialCarrierAsync(string number, string? slotId, CancellationToken ct);
    Task<SipGatewayCarrierCall> DialCarrierAsync(string number, string? slotId, CancellationToken ct,
        Action<string, string?> onStarted) => DialCarrierAsync(number, slotId, ct);
    Task<SipGatewayCarrierCall> AnswerCarrierAsync(string? slotId, CancellationToken ct);
    Task<SipGatewayCarrierCall> AnswerCarrierCallAsync(string callId, string? slotId, CancellationToken ct) => AnswerCarrierAsync(slotId, ct);
    Task HangupCarrierCallAsync(string callId, string? slotId, CancellationToken ct) => HangupAsync(slotId, ct);
    Task HangupAsync(string? slotId, CancellationToken ct);
    Task SendDtmfAsync(char digit, string? slotId, CancellationToken ct);
    ImsCallManager? GetImsMedia(string? slotId);
}

internal sealed class KernelSipGatewayCallController : ISipGatewayCallController, ISipGatewaySimBoundMessageController, IDisposable
{
    private readonly IVoKernel _kernel;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _dialAdmissions = new(StringComparer.OrdinalIgnoreCase);
    internal SemaphoreSlim GetDialAdmission(string? slotId) =>
        _dialAdmissions.GetOrAdd(slotId ?? "\0legacy-default", _ => new(1, 1));

    public KernelSipGatewayCallController(IVoKernel kernel)
    {
        _kernel = kernel;
        _kernel.IncomingCall += ForwardIncoming;
        _kernel.CallEnded += ForwardEnded;
        _kernel.SmsReceived += ForwardSms;
    }

    public event EventHandler<IncomingCallEventArgs>? IncomingCall;
    public event EventHandler<CallEndedEventArgs>? CallEnded;
    public event EventHandler<SmsReceivedEventArgs>? SmsReceived;
    public string? GetSmsSimBinding(string slotId) => _kernel.GetSmsSimBinding(slotId);
    public Task<SmsSubmitResult> SendSmsForSimAsync(string number, string text, string slotId, string expectedBinding, CancellationToken ct) =>
        _kernel.SendSmsForSimAsync(number, text, slotId, expectedBinding, ct);
    public Task<SmsSubmitResult> SendSmsAsync(string number, string text, string? slotId, CancellationToken ct)
    {
        // Resolve once. Never fall back to a different SIM after hotplug.
        var slot = ResolveSlot(slotId, number) ?? throw new InvalidOperationException("No SMS modem is configured.");
        return _kernel.SendSmsAsync(number, text, requestStatusReport: slot.PreferredTelephonyMode != TelephonyMode.HostIms,
            slotId: slot.Id, ct: ct);
    }

    public Task<SipGatewayCarrierCall> DialCarrierAsync(string number, string? slotId, CancellationToken ct) =>
        DialCarrierAsync(number, slotId, ct, (_, _) => { });

    public async Task<SipGatewayCarrierCall> DialCarrierAsync(string number, string? slotId, CancellationToken ct,
        Action<string, string?> onStarted)
    {
        var slot = ResolveSlot(slotId, number);
        var admission = GetDialAdmission(slot?.Id);
        await admission.WaitAsync(ct).ConfigureAwait(false);
        var ownedCalls = slot?.Calls ?? _kernel.Calls;
        void Started(object? sender, CallStateChangedEventArgs e)
        {
            if (e.IsOutgoing && e.NewState == CallState.Dialing) onStarted(e.CallId, slot?.Id);
        }
        ownedCalls.CallStateChanged += Started;
        try
        {
        if (slot is not null && (!_kernel.Pool.Slots.TryGetValue(slot.Id, out var current) || !ReferenceEquals(current, slot)))
            throw new KeyNotFoundException("The original SIP carrier modem is no longer available; no other line was used.");
        var voWifi = slot?.VoWifi ?? _kernel.VoWifi;
        var calls = slot?.Calls ?? _kernel.Calls;
        var route = ResolveCarrierRoute(slot);
        if (route == TelephonyMode.VoWifi)
        {
            if (calls.State is not (CallState.Idle or CallState.Ended))
                throw new InvalidOperationException("The selected IMS line already has a call.");
            calls.ExternalMediaBridgeEnabled = true;
            try
            {
                var imsCall = await calls.DialAsync(number, voWifi, ct).ConfigureAwait(false);
                return new SipGatewayCarrierCall(imsCall, slot?.Id, calls);
            }
            catch
            {
                if (calls.State is CallState.Dialing or CallState.Ringing or CallState.Active)
                {
                    try { await calls.HangupAsync().ConfigureAwait(false); } catch { }
                }
                calls.ExternalMediaBridgeEnabled = false;
                throw;
            }
        }
        if (route == TelephonyMode.HostIms && slot != null)
        {
            if (calls.State is not (CallState.Idle or CallState.Ended))
                throw new InvalidOperationException("The selected Host IMS line already has a call.");
            calls.ExternalMediaBridgeEnabled = true;
            try
            {
                var imsCall = await slot.DialHostImsAsync(number, ct).ConfigureAwait(false);
                return new SipGatewayCarrierCall(imsCall, slot.Id, calls);
            }
            catch
            {
                if (calls.State is CallState.Dialing or CallState.Ringing or CallState.Active)
                {
                    try { await calls.HangupAsync().ConfigureAwait(false); } catch { }
                }
                calls.ExternalMediaBridgeEnabled = false;
                throw;
            }
        }
        if (slot?.Modem is not { IsOpen: true } || _kernel.CellularSipMediaProvider is not { } provider)
            throw new InvalidOperationException("The selected line has neither registered VoWiFi IMS nor a cellular USB-audio bridge.");
        if (slot.HasCellularCall)
            throw new InvalidOperationException("The selected cellular line already has a call.");

        provider.Reserve(slot.Id);
        try
        {
            var connected = WaitForCellularConnectedAsync(slot, ct);
            var call = await slot.DialCellularAsync(number, ct).ConfigureAwait(false);
            await connected.ConfigureAwait(false);
            var media = await provider.OpenAsync(slot.Id, ct).ConfigureAwait(false);
            return new SipGatewayCarrierCall(call with { State = CallState.Active, ConnectedAt = DateTime.UtcNow }, slot.Id, media);
        }
        catch
        {
            // ATD may already have created a live baseband call even when media
            // startup or the SIP transaction is cancelled. Never leave that
            // carrier leg orphaned after returning an error to the SIP phone.
            try { _ = await _kernel.HangupAsync(slot.Id, CancellationToken.None).ConfigureAwait(false); } catch { }
            await provider.ReleaseAsync(slot.Id).ConfigureAwait(false);
            throw;
        }
        }
        finally
        {
            ownedCalls.CallStateChanged -= Started;
            admission.Release();
        }
    }

    public async Task<SipGatewayCarrierCall> AnswerCarrierAsync(string? slotId, CancellationToken ct)
    {
        var slot = ResolveSlot(slotId, null);
        var calls = slot?.Calls ?? _kernel.Calls;
        if (calls.State == CallState.Ringing && calls.IsIncoming)
        {
            calls.ExternalMediaBridgeEnabled = true;
            var expectedId = calls.ActiveCall?.CallId;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Connected(object? sender, CallConnectedEventArgs e) { if (e.CallId == expectedId) ready.TrySetResult(); }
            void Ended(object? sender, CallEndedEventArgs e) { if (e.CallId == expectedId) ready.TrySetException(new InvalidOperationException("Carrier call ended before ACK.")); }
            calls.CallConnected += Connected;
            calls.CallEnded += Ended;
            try
            {
                await calls.AnswerAsync(ct).ConfigureAwait(false);
                // AnswerAsync sends 200; media is not active until the operator ACK.
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(32), ct).ConfigureAwait(false);
                return new SipGatewayCarrierCall(calls.ActiveCall ?? throw new InvalidOperationException("Carrier call ended."), slot?.Id, calls);
            }
            catch { calls.ExternalMediaBridgeEnabled = false; throw; }
            finally { calls.CallConnected -= Connected; calls.CallEnded -= Ended; }
        }
        if (slot == null || !slot.HasCellularCall || _kernel.CellularSipMediaProvider is not { } provider)
            throw new InvalidOperationException("No bridgeable incoming carrier call is available.");

        provider.Reserve(slot.Id);
        try
        {
            var connected = WaitForCellularConnectedAsync(slot, ct);
            var call = await slot.AnswerCallAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The modem did not accept the incoming call.");
            await connected.ConfigureAwait(false);
            var media = await provider.OpenAsync(slot.Id, ct).ConfigureAwait(false);
            return new SipGatewayCarrierCall(call with { State = CallState.Active, ConnectedAt = DateTime.UtcNow }, slot.Id, media);
        }
        catch
        {
            try { _ = await _kernel.HangupAsync(slot.Id, CancellationToken.None).ConfigureAwait(false); } catch { }
            await provider.ReleaseAsync(slot.Id).ConfigureAwait(false);
            throw;
        }
    }

    private bool IsCurrentCall(string callId, string? slotId)
    {
        var slot = ResolveSlot(slotId, null);
        return string.Equals((slot?.Calls ?? _kernel.Calls).ActiveCall?.CallId, callId, StringComparison.OrdinalIgnoreCase) ||
            slot?.IsCurrentCellularCall(callId) == true;
    }

    public Task<SipGatewayCarrierCall> AnswerCarrierCallAsync(string callId, string? slotId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsCurrentCall(callId, slotId)) throw new InvalidOperationException("The original incoming carrier call is no longer available.");
        return AnswerCarrierAsync(slotId, ct);
    }

    public Task HangupCarrierCallAsync(string callId, string? slotId, CancellationToken ct) =>
        IsCurrentCall(callId, slotId) ? HangupAsync(slotId, ct) : Task.CompletedTask;

    public async Task HangupAsync(string? slotId, CancellationToken ct)
    {
        var provider = _kernel.CellularSipMediaProvider;
        // Capture ownership before ATH emits CallEnded; the application event
        // handler is allowed to clear the reservation as part of its cleanup.
        var releaseCellularMedia = provider?.IsReserved(slotId) == true;
        _ = await _kernel.HangupAsync(slotId, ct).ConfigureAwait(false);
        if (releaseCellularMedia && provider != null)
            await provider.ReleaseAsync(slotId).ConfigureAwait(false);
    }

    public async Task SendDtmfAsync(char digit, string? slotId, CancellationToken ct) =>
        _ = await _kernel.SendDtmfAsync(digit, slotId, ct).ConfigureAwait(false);

    public ImsCallManager? GetImsMedia(string? slotId) => ResolveSlot(slotId, null)?.Calls ?? _kernel.Calls;

    internal TelephonyMode ResolveCarrierRoute(Pool.ModemSlot? slot) =>
        TelephonyRoutePolicy.Resolve(slot?.PreferredTelephonyMode, slot?.TelephonyEnabled ?? true,
            (slot?.VoWifi ?? _kernel.VoWifi).State == VoWifiState.ImsRegistered,
            slot?.GetHostImsRegistrationStatus().IsRegistered == true, slot?.Modem?.IsOpen == true,
            cellularImsMode: slot?.CellularImsMode);

    private Pool.ModemSlot? ResolveSlot(string? slotId, string? number)
    {
        if (!string.IsNullOrWhiteSpace(slotId))
            return _kernel.Pool.Slots.TryGetValue(slotId, out var requested)
                ? requested
                : throw new KeyNotFoundException($"SIP gateway line '{slotId}' is unavailable.");
        return !string.IsNullOrWhiteSpace(number)
            ? _kernel.Pool.FindSlotForTarget(number) ?? _kernel.Pool.ActiveSlot
            : _kernel.Pool.ActiveSlot;
    }

    private static async Task WaitForCellularConnectedAsync(Pool.ModemSlot slot, CancellationToken ct)
    {
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnConnected(object? sender, CallConnectedEventArgs args)
        {
            if (args.SlotId == slot.Id && args.Codec?.StartsWith("Cellular", StringComparison.OrdinalIgnoreCase) == true)
                connected.TrySetResult();
        }
        void OnEnded(object? sender, CallEndedEventArgs args)
        {
            if (args.SlotId == slot.Id) ended.TrySetResult(args.Reason ?? "ended");
        }
        slot.CallConnected += OnConnected;
        slot.CallEnded += OnEnded;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var winner = await Task.WhenAny(connected.Task, ended.Task).WaitAsync(timeout.Token).ConfigureAwait(false);
            if (winner == ended.Task) throw new InvalidOperationException($"Cellular call ended before media became active: {await ended.Task.ConfigureAwait(false)}");
            await connected.Task.ConfigureAwait(false);
        }
        finally
        {
            slot.CallConnected -= OnConnected;
            slot.CallEnded -= OnEnded;
        }
    }

    private void ForwardIncoming(object? sender, IncomingCallEventArgs e) => IncomingCall?.Invoke(this, e);
    private void ForwardEnded(object? sender, CallEndedEventArgs e) => CallEnded?.Invoke(this, e);
    private void ForwardSms(object? sender, SmsReceivedEventArgs e) => SmsReceived?.Invoke(this, e);

    public void Dispose()
    {
        _kernel.IncomingCall -= ForwardIncoming;
        _kernel.CallEnded -= ForwardEnded;
        _kernel.SmsReceived -= ForwardSms;
    }
}
