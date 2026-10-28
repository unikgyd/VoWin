using VoSharp.Sim;
using VoSharp.Modem.At;
using VoSharp.Telephony;
using VoSharp.Telephony.Sms;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Kernel.Pool;

public partial class ModemSlot
{
    private readonly object _smsIdentityGate = new();
    private CancellationTokenSource _smsIdentityCancellation = new();
    private long _smsIdentityGeneration;
    private int _smsMutations;
    internal sealed record SmsOwner(ModemSlot Slot, SimIdentity Identity, long Generation, CancellationToken Cancellation)
    {
        public override string ToString() => "SMS source owner (identity omitted)";
    }

    internal SmsOwner CaptureSmsOwner(string expectedBinding)
    {
        if (!SmsSubscriptionIdentity.IsValid(expectedBinding)) throw new ArgumentException("The original SMS SIM binding is invalid.");
        lock (_smsIdentityGate)
        {
            if (_sim is not { } identity || IsProfileSwitchInProgress || Volatile.Read(ref _smsMutations) != 0 ||
                SmsSubscriptionIdentity.ForIccid(identity.Iccid) != expectedBinding)
                throw new InvalidOperationException("The original SMS SIM is no longer selected; no other SIM was used.");
            return new(this, identity, _smsIdentityGeneration, _smsIdentityCancellation.Token);
        }
    }

    private void PublishSmsIdentity(SimIdentity? identity)
    {
        CancellationTokenSource? old = null;
        lock (_smsIdentityGate)
        {
            if (_sim?.Iccid != identity?.Iccid || _sim?.Imsi != identity?.Imsi)
            {
                old = _smsIdentityCancellation;
                _smsIdentityCancellation = new();
                _smsIdentityGeneration++;
            }
            _sim = identity;
        }
        if (old != null)
        {
            VoWifi.InvalidateSmsSubscription();
            try { old.Cancel(); } finally { old.Dispose(); }
        }
    }

    private void InvalidateSmsIdentity()
    {
        CancellationTokenSource old;
        lock (_smsIdentityGate)
        {
            old = _smsIdentityCancellation;
            _smsIdentityCancellation = new();
            _smsIdentityGeneration++;
        }
        try { old.Cancel(); } finally { old.Dispose(); }
    }

    public string? GetSmsSimBinding()
    {
        lock (_smsIdentityGate)
            return IsProfileSwitchInProgress || Volatile.Read(ref _smsMutations) != 0 ? null : SmsSubscriptionIdentity.ForIccid(_sim?.Iccid);
    }

    private IDisposable BeginSmsSourceMutation()
    {
        Interlocked.Increment(ref _smsMutations);
        InvalidateSmsIdentity();
        VoWifi.InvalidateSmsSubscription();
        return new SmsMutation(this);
    }
    private sealed class SmsMutation(ModemSlot owner) : IDisposable
    {
        private int _ended;
        public void Dispose() { if (Interlocked.Exchange(ref _ended, 1) == 0) Interlocked.Decrement(ref owner._smsMutations); }
    }
    internal async Task<T> RunSmsSourceMutationAsync<T>(Func<CancellationToken, Task<T>> mutate, CancellationToken ct = default)
    {
        using var mutation = BeginSmsSourceMutation();
        await _profileSwitchGate.WaitAsync(ct).ConfigureAwait(false);
        try { ct.ThrowIfCancellationRequested(); return await mutate(ct).ConfigureAwait(false); }
        finally { _profileSwitchGate.Release(); }
    }

    public Task<bool> SwitchHardwareSimSlotAsync(int index, CancellationToken ct = default)
    {
        if (index is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(index));
        return RunSmsSourceMutationAsync(async token =>
        {
            if (Modem is not { IsOpen: true } modem || IsPcscReader) return false;
            await StopHostImsRegistrationLocallyAsync().ConfigureAwait(false);
            await VoWifi.StopVoWifiAsync(token).ConfigureAwait(false);
            Sim = null;
            if (!await modem.SetSimSlotAsync(index, token).ConfigureAwait(false)) return false;
            return await RefreshSimCoreAsync(token).ConfigureAwait(false);
        }, ct);
    }

    internal static bool IsReadOnlySmsSafeAt(string command) => command.Trim().ToUpperInvariant() is
        "AT" or "ATI" or "AT+CSQ" or "AT+CESQ" or "AT+CIMI" or "AT+CCID" or "AT+QCCID" or
        "AT+CGSN" or "AT+CGMI" or "AT+CGMM" or "AT+CGMR" or "AT+CPIN?" or "AT+CFUN?" or
        "AT+COPS?" or "AT+CREG?" or "AT+CEREG?" or "AT+CGREG?" or "AT+QDSIM?" or "AT+QNWINFO" or "AT+QCSQ";

    public async Task<AtResponse> SendRawAtSmsSafeAsync(string command, int timeoutMs = 5000, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (IsReadOnlySmsSafeAt(command))
        {
            await _profileSwitchGate.WaitAsync(ct).ConfigureAwait(false);
            try { return await (Modem ?? throw new InvalidOperationException("No modem is available.")).SendRawAtCommandAsync(command, timeoutMs, ct).ConfigureAwait(false); }
            finally { _profileSwitchGate.Release(); }
        }
        return await RunSmsSourceMutationAsync(async token =>
        {
            var modem = Modem ?? throw new InvalidOperationException("No modem is available.");
            await StopHostImsRegistrationLocallyAsync().ConfigureAwait(false);
            await VoWifi.StopVoWifiAsync(token).ConfigureAwait(false);
            Sim = null;
            try { return await modem.SendRawAtCommandAsync(command, timeoutMs, token).ConfigureAwait(false); }
            finally { Sim = null; } // Unknown debug writes require an explicit verified SIM refresh.
        }, ct).ConfigureAwait(false);
    }

    // Production and offline fixtures share this exact admission/identity lease.
    internal async Task<SmsSubmitResult> SendSmsForSimWithAsync(string expectedBinding,
        Func<SimIdentity, Action, CancellationToken, Task<SmsSubmitResult>> send, CancellationToken ct = default, SmsOwner? capturedOwner = null)
    {
        var owner = capturedOwner ?? CaptureSmsOwner(expectedBinding); // Pin before any asynchronous queue.
        if (!ReferenceEquals(owner.Slot, this) || SmsSubscriptionIdentity.ForIccid(owner.Identity.Iccid) != expectedBinding)
            throw new InvalidOperationException("The captured SMS owner does not match the original modem and SIM.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, owner.Cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        ct = deadline.Token;
        await _profileSwitchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            void Verify()
            {
                ct.ThrowIfCancellationRequested();
                lock (_smsIdentityGate)
                    if (_smsIdentityGeneration != owner.Generation || _sim?.Iccid != owner.Identity.Iccid || _sim?.Imsi != owner.Identity.Imsi || IsProfileSwitchInProgress || Volatile.Read(ref _smsMutations) != 0)
                        throw new InvalidOperationException("The original SMS SIM changed during submission; no automatic retry is safe.");
            }
            Verify();
            return await send(owner.Identity, Verify, ct).ConfigureAwait(false);
        }
        finally { _profileSwitchGate.Release(); }
    }

    public Task<SmsSubmitResult> SendSmsForSimAsync(string recipient, string text, string expectedBinding, CancellationToken ct = default) =>
        SendSmsForSimAsync(recipient, text, expectedBinding, CaptureSmsOwner(expectedBinding), ct);

    internal Task<SmsSubmitResult> SendSmsForSimAsync(string recipient, string text, string expectedBinding, SmsOwner owner, CancellationToken ct) =>
        SendSmsForSimWithAsync(expectedBinding, async (identity, verify, token) =>
        {
            if (!TelephonyEnabled) throw new InvalidOperationException("The original SMS line is disabled.");
            var route = TelephonyRoutePolicy.Resolve(PreferredTelephonyMode, TelephonyEnabled,
                VoWifi.State == VoWifiState.ImsRegistered, GetHostImsRegistrationStatus().IsRegistered,
                Sms != null, cellularImsMode: CellularImsMode);
            verify();
            if (route == TelephonyMode.HostIms)
                return await SendHostImsSmsForIdentityAsync(recipient, text, identity, verify, token).ConfigureAwait(false);
            if (route == TelephonyMode.VoWifi)
                return await VoWifi.SendSmsForSubscriptionAsync(recipient, text, identity.Imsi, identity.Iccid, token).ConfigureAwait(false);
            if (route != TelephonyMode.ModemBridge || Modem is not { IsOpen: true } modem || Sms is not { } sms)
                throw new InvalidOperationException("The original SMS transport is unavailable; no fallback was used.");
            void VerifyAt()
            {
                verify();
                if (!ReferenceEquals(Modem, modem) || !ReferenceEquals(Sms, sms) || !modem.IsOpen)
                    throw new InvalidOperationException("The original SMS modem is no longer available.");
            }
            return await sms.SendSmsDetailedGuardedAsync(recipient, text, VerifyAt, ct: token).ConfigureAwait(false);
        }, ct, owner);

    private async Task<SmsSubmitResult> SendHostImsSmsForIdentityAsync(string recipient, string text,
        SimIdentity identity, Action verify, CancellationToken ct)
    {
        await _hostImsRegistrationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = _hostImsRegistration;
            var modem = Modem;
            verify();
            if (client?.IsRegistered != true || modem is not { IsOpen: true } ||
                _hostImsRegisteredIccid != identity.Iccid || _hostImsRegisteredImsi != identity.Imsi ||
                !HostImsCardMatchesCurrentSlot() || Volatile.Read(ref _hostImsPppLease)?.IsLost == true)
                throw new InvalidOperationException("The original SIM has no stable Host IMS SMS session.");
            var smsc = await modem.GetSmsCenterAsync(ct).ConfigureAwait(false);
            verify();
            if (!ReferenceEquals(client, _hostImsRegistration) || !HostImsCardMatchesCurrentSlot() || string.IsNullOrWhiteSpace(smsc))
                throw new InvalidOperationException("The original Host IMS SMS source is unavailable.");
            return await client.SendSmsOverImsAsync(recipient, text, smsc, requestStatusReport: false, ct).ConfigureAwait(false);
        }
        finally { _hostImsRegistrationGate.Release(); }
    }
}
