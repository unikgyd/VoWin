using VoSharp.Telephony.Calls;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Kernel.SipGateway;

public sealed record SipGatewayCarrierCall(CallInfo Call, string? SlotId, ICallPcmMedia Media);

public interface ISipGatewayCallController
{
    event EventHandler<IncomingCallEventArgs>? IncomingCall;
    event EventHandler<CallEndedEventArgs>? CallEnded;

    Task<SipGatewayCarrierCall> DialCarrierAsync(string number, string? slotId, CancellationToken ct);
    Task<SipGatewayCarrierCall> AnswerCarrierAsync(string? slotId, CancellationToken ct);
    Task HangupAsync(string? slotId, CancellationToken ct);
    Task SendDtmfAsync(char digit, string? slotId, CancellationToken ct);
    ImsCallManager? GetImsMedia(string? slotId);
}

internal sealed class KernelSipGatewayCallController : ISipGatewayCallController, IDisposable
{
    private readonly IVoKernel _kernel;
    private readonly SemaphoreSlim _dialAdmission = new(1, 1);

    public KernelSipGatewayCallController(IVoKernel kernel)
    {
        _kernel = kernel;
        _kernel.IncomingCall += ForwardIncoming;
        _kernel.CallEnded += ForwardEnded;
    }

    public event EventHandler<IncomingCallEventArgs>? IncomingCall;
    public event EventHandler<CallEndedEventArgs>? CallEnded;

    public async Task<SipGatewayCarrierCall> DialCarrierAsync(string number, string? slotId, CancellationToken ct)
    {
        await _dialAdmission.WaitAsync(ct).ConfigureAwait(false);
        try
        {
        var slot = ResolveSlot(slotId, number);
        var voWifi = slot?.VoWifi ?? _kernel.VoWifi;
        var calls = slot?.Calls ?? _kernel.Calls;
        if (voWifi.State == VoWifiState.ImsRegistered)
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
        if (slot?.GetHostImsRegistrationStatus().IsRegistered == true)
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
            _dialAdmission.Release();
        }
    }

    public async Task<SipGatewayCarrierCall> AnswerCarrierAsync(string? slotId, CancellationToken ct)
    {
        var slot = ResolveSlot(slotId, null);
        var calls = slot?.Calls ?? _kernel.Calls;
        if (calls.State == CallState.Ringing && calls.IsIncoming)
        {
            calls.ExternalMediaBridgeEnabled = true;
            var imsCall = await calls.AnswerAsync(ct).ConfigureAwait(false);
            return new SipGatewayCarrierCall(imsCall, slot?.Id, calls);
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

    public void Dispose()
    {
        _kernel.IncomingCall -= ForwardIncoming;
        _kernel.CallEnded -= ForwardEnded;
    }
}
