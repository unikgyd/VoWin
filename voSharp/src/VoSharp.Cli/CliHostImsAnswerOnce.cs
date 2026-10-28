using VoSharp.Kernel;
using VoSharp.Telephony.Calls;

/// <summary>
/// A short-lived, explicit lab latch. It never answers a different caller,
/// a non-Host-IMS call, or more than one matching call.
/// </summary>
internal sealed class CliHostImsAnswerOnce(VoKernel kernel, CliMicrophoneCapture microphone)
{
    private sealed record Arm(string SlotId, string Caller, DateTime ExpiresAtUtc, bool StartMicrophone);
    private readonly object _gate = new();
    private Arm? _armed;
    private (string CallId, string SlotId)? _pendingMicrophone;

    public string ArmForCaller(string caller, bool startMicrophone)
    {
        var normalized = ImsCallerNumberMatcher.Normalize(caller)
            ?? throw new ArgumentException("Provide one exact 11-digit Chinese mobile number or +86 equivalent.");
        var slot = kernel.Pool.ActiveSlot;
        if (slot is null || !slot.GetHostImsRegistrationStatus().IsRegistered)
            throw new InvalidOperationException("A registered Host IMS slot is required to arm one-shot answering.");
        if (slot.Calls.ActiveCall is not null)
            throw new InvalidOperationException("Finish the current call before arming one-shot answering.");
        lock (_gate)
        {
            _armed = new Arm(slot.Id, normalized, DateTime.UtcNow.AddMinutes(10), startMicrophone);
            _pendingMicrophone = null;
        }
        return $"One-shot Host IMS answer armed for caller ***{normalized[^4..]} on {slot.Id} for 10 minutes" +
            (startMicrophone ? "; host microphone will start only after matching SIP ACK." : ".");
    }

    public string Status()
    {
        lock (_gate)
        {
            if (_armed is not { } arm || DateTime.UtcNow >= arm.ExpiresAtUtc)
            {
                _armed = null;
                return "One-shot Host IMS answer is off.";
            }
            return $"Armed for ***{arm.Caller[^4..]} on {arm.SlotId} until {arm.ExpiresAtUtc:HH:mm:ss} UTC" +
                (arm.StartMicrophone ? "; microphone after ACK." : ".");
        }
    }

    public string Disarm()
    {
        lock (_gate)
        {
            _armed = null;
            _pendingMicrophone = null;
        }
        return "One-shot Host IMS answer disarmed.";
    }

    public bool OnIncoming(IncomingCallEventArgs incoming)
    {
        if (incoming.SlotId is null ||
            !kernel.Pool.Slots.TryGetValue(incoming.SlotId, out var slot) ||
            !slot.Calls.IsHostImsCall || slot.Calls.State != CallState.Ringing ||
            !string.Equals(slot.Calls.ActiveCall?.CallId, incoming.CallId, StringComparison.OrdinalIgnoreCase))
            return false;

        Arm? consumed;
        lock (_gate)
        {
            consumed = _armed;
            if (consumed is null || DateTime.UtcNow >= consumed.ExpiresAtUtc ||
                !string.Equals(consumed.SlotId, incoming.SlotId, StringComparison.OrdinalIgnoreCase) ||
                !ImsCallerNumberMatcher.Matches(consumed.Caller, incoming.CallerNumber))
            {
                if (consumed is not null && DateTime.UtcNow >= consumed.ExpiresAtUtc) _armed = null;
                return false;
            }
            _armed = null;
            if (consumed.StartMicrophone)
                _pendingMicrophone = (incoming.CallId, incoming.SlotId);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await kernel.AnswerAsync(incoming.SlotId).ConfigureAwait(false);
                Console.WriteLine("[Host IMS] One-shot answer sent; waiting for matching SIP ACK.");
            }
            catch (Exception ex)
            {
                lock (_gate) _pendingMicrophone = null;
                Console.WriteLine($"[Host IMS] One-shot answer failed: {ex.Message}");
            }
        });
        return true;
    }

    public void OnConnected(CallConnectedEventArgs connected)
    {
        lock (_gate)
        {
            if (_pendingMicrophone is not { } pending ||
                !string.Equals(pending.CallId, connected.CallId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(pending.SlotId, connected.SlotId, StringComparison.OrdinalIgnoreCase))
                return;
            _pendingMicrophone = null;
        }
        _ = Task.Run(() =>
        {
            try { Console.WriteLine($"[Host IMS] {microphone.Start()}"); }
            catch (Exception ex) { Console.WriteLine($"[Host IMS] Microphone did not start: {ex.Message}"); }
        });
    }

    public void OnEnded(CallEndedEventArgs ended)
    {
        lock (_gate)
            if (_pendingMicrophone is { } pending &&
                string.Equals(pending.CallId, ended.CallId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(pending.SlotId, ended.SlotId, StringComparison.OrdinalIgnoreCase))
                _pendingMicrophone = null;
    }
}
