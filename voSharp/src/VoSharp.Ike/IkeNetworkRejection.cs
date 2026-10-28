using System.Buffers.Binary;

namespace VoSharp.Ike;

public enum IkeBackoffTimerStatus { Missing, Invalid, Timed, Deactivated }

/// <summary>Tw3 uses GPRS Timer 3, including its distinct deactivated state.</summary>
public sealed record IkeBackoffTimer
{
    public IkeBackoffTimerStatus Status { get; }
    public byte? EncodedValue { get; }
    public TimeSpan? Duration { get; }

    private IkeBackoffTimer(IkeBackoffTimerStatus status, byte? encoded = null, TimeSpan? duration = null)
        => (Status, EncodedValue, Duration) = (status, encoded, duration);

    public static IkeBackoffTimer Missing { get; } = new(IkeBackoffTimerStatus.Missing);
    public static IkeBackoffTimer Invalid { get; } = new(IkeBackoffTimerStatus.Invalid);

    // TS 24.302 8.2.9.1 requires both the length octet and the timer octet.
    public static IkeBackoffTimer Decode(ReadOnlySpan<byte> data)
        => data.Length == 2 && data[0] == 1 ? DecodeValue(data[1]) : Invalid;

    internal static IkeBackoffTimer DecodeValue(byte encoded)
    {
        var unit = encoded >> 5;
        if (unit == 7) return new(IkeBackoffTimerStatus.Deactivated, encoded);
        // Unit 6's 320-hour exception applies to extended periodic timers,
        // not Tw3. Its ordinary interpretation here is one hour.
        var seconds = unit switch { 0 => 600, 1 => 3600, 2 => 36000, 3 => 2, 4 => 30, 5 => 60, _ => 3600 };
        return new(IkeBackoffTimerStatus.Timed, encoded, TimeSpan.FromSeconds(seconds * (encoded & 31)));
    }
}

public enum IkeRejectionScope { Epdg, PlmnApn, Plmn }

/// <summary>
/// A coherent rejection on the current IKE SA, authenticated by initial or final AUTH.
/// Carries protocol evidence only; this is not a persistent reconnect gate.
/// The manager must bind its scope to the actual card, PLMN, APN and ePDG.
/// </summary>
public sealed record IkeNetworkRejection(ushort NotifyType, IkeRejectionScope Scope, IkeBackoffTimer BackoffTimer)
{
    // A true shape probe must not escape as evidence before current AUTH verifies.
    internal static IkeNetworkRejection? Capture(IReadOnlyList<IkePayload> payloads, bool currentResponderAuthenticated)
    {
        if (!currentResponderAuthenticated) return null;
        ushort? cause = null;
        var errors = 0;
        var timers = 0;
        var timer = IkeBackoffTimer.Missing;
        foreach (var payload in payloads.Where(p => p.Type == IkePayloadType.Notify))
        {
            if (payload.Body.Length < 4 || payload.Body[1] > payload.Body.Length - 4) return null;
            var kind = BinaryPrimitives.ReadUInt16BigEndian(payload.Body.AsSpan(2));
            if (kind < 16384)
            {
                errors++;
                if (ScopeFor(kind) == null || payload.Body[1] != 0 || payload.Body.Length != 4) return null;
                cause = kind;
            }
            if (kind == 41041)
            {
                timers++;
                timer = timers == 1 && payload.Body[1] == 0
                    ? IkeBackoffTimer.Decode(payload.Body.AsSpan(4)) : IkeBackoffTimer.Invalid;
            }
        }
        return errors == 1 && cause is { } type ? new(type, ScopeFor(type)!.Value, timer) : null;
    }

    private static IkeRejectionScope? ScopeFor(ushort kind) => kind switch
    {
        10500 => IkeRejectionScope.Epdg, // NETWORK_FAILURE
        9002 => IkeRejectionScope.PlmnApn, // NO_APN_SUBSCRIPTION
        9000 or 9001 or 9003 or 9006 or 11001 or 11011 => IkeRejectionScope.Plmn,
        _ => null
    };
}
