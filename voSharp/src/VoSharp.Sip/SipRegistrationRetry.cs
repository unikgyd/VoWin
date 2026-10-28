using System.Globalization;

namespace VoSharp.Sip;

/// <summary>Safe, structured registrar feedback. Never contains a SIP identity or raw header.</summary>
public sealed record SipRegistrationFailureInfo(int StatusCode, uint? RetryAfterSeconds,
    bool InvalidRetryAfter, bool RequiresNewFlow, DateTime ReceivedAtUtc)
{
    public bool PauseAutomaticRecovery => InvalidRetryAfter || StatusCode == 403 && RetryAfterSeconds is null;

    internal static SipRegistrationFailureInfo FromResponse(SipMessage response, bool newFlow)
    {
        uint? seconds = null;
        var invalid = false;
        if (response.Headers.TryGetValue("Retry-After", out var values))
        {
            uint parsed = 0;
            invalid = values.Count != 1 || !TryParseRetryAfter(values[0], out parsed);
            if (!invalid) seconds = parsed;
        }
        return new(response.StatusCode, seconds, invalid, newFlow, DateTime.UtcNow);
    }

    // RFC 3261 20.33 / 25: delta-seconds, optional nested comment, then parameters.
    // Reject lists, duplicate parameters, overflow and malformed quoting. Do not
    // interpret the duration parameter as an additional delay or retain comments.
    internal static bool TryParseRetryAfter(string? value, out uint seconds)
    {
        seconds = 0;
        if (string.IsNullOrEmpty(value) || value.Length > 4096 || value.Any(c => char.IsControl(c) && c != '\t')) return false;
        var i = 0;
        void Space() { while (i < value.Length && value[i] is ' ' or '\t') i++; }
        Space();
        var start = i;
        while (i < value.Length && char.IsAsciiDigit(value[i])) i++;
        if (start == i || !uint.TryParse(value.AsSpan(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture, out seconds)) return false;
        Space();
        if (i < value.Length && value[i] == '(')
        {
            i++;
            var depth = 1;
            while (i < value.Length && depth > 0)
            {
                var c = value[i++];
                if (c == '\\') { if (i == value.Length) return false; i++; }
                else if (c == '(') { if (++depth > 16) return false; }
                else if (c == ')') depth--;
            }
            if (depth != 0) return false;
            Space();
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (i < value.Length)
        {
            if (value[i++] != ';') return false;
            Space();
            start = i;
            while (i < value.Length && Token(value[i])) i++;
            if (start == i) return false;
            var name = value[start..i];
            if (!names.Add(name)) return false;
            Space();
            var duration = name.Equals("duration", StringComparison.OrdinalIgnoreCase);
            if (i == value.Length || value[i] != '=') { if (duration) return false; continue; }
            i++;
            Space();
            start = i;
            if (i < value.Length && value[i] == '"')
            {
                if (duration) return false;
                i++;
                var closed = false;
                while (i < value.Length)
                {
                    var c = value[i++];
                    if (c == '\\') { if (i == value.Length) return false; i++; }
                    else if (c == '"') { closed = true; break; }
                }
                if (!closed) return false;
            }
            else
            {
                while (i < value.Length && (Token(value[i]) || value[i] is ':' or '[' or ']')) i++;
                if (start == i || duration && !uint.TryParse(value.AsSpan(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture, out _)) return false;
            }
            Space();
        }
        return true;
    }

    private static bool Token(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '!' or '%' or '*' or '_' or '+' or '`' or '\'' or '~';
}

public sealed class SipRegistrationRetryBlockedException(string message, DateTime? retryNotBeforeUtc, bool paused)
    : InvalidOperationException(message)
{
    public DateTime? RetryNotBeforeUtc { get; } = retryNotBeforeUtc;
    public bool AutomaticRecoveryPaused { get; } = paused;
}

/// <summary>
/// An owner retains this gate across registration flows. Scope keys identify a
/// SIM within that owner; they are never logged. Waiting uses monotonic time,
/// so a wall-clock correction or a network-availability event cannot skip it.
/// </summary>
public sealed class SipRegistrationRetryGate(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SipRegistrationFailureInfo> _feedback = new(StringComparer.Ordinal);
    private sealed record Entry(long Started, TimeSpan Delay, DateTime Deadline, bool Paused);

    public void Observe(string scope, SipRegistrationFailureInfo failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        lock (_sync)
        {
            _feedback[scope] = failure;
            if (failure.RetryAfterSeconds is null && !failure.PauseAutomaticRecovery) return;
            var delay = TimeSpan.FromSeconds(failure.RetryAfterSeconds ?? 0);
            var proposed = new Entry(_time.GetTimestamp(), delay, _time.GetUtcNow().UtcDateTime.Add(delay), failure.PauseAutomaticRecovery);
            if (_entries.TryGetValue(scope, out var old))
                proposed = Remaining(old) > delay
                    ? old with { Paused = old.Paused || proposed.Paused }
                    : proposed with { Paused = old.Paused || proposed.Paused };
            _entries[scope] = proposed;
        }
    }

    public SipRegistrationFailureInfo? GetFailure(string scope)
    {
        lock (_sync) return _feedback.GetValueOrDefault(scope);
    }

    public void ClearFailure(string scope)
    {
        lock (_sync) _feedback.Remove(scope);
    }

    public DateTime? RetryNotBeforeUtc(string scope)
    {
        lock (_sync) return _entries.TryGetValue(scope, out var entry) && !entry.Paused && Remaining(entry) > TimeSpan.Zero
            ? _time.GetUtcNow().UtcDateTime.Add(Remaining(entry)) : null;
    }

    public bool IsPaused(string scope)
    {
        lock (_sync) return _entries.TryGetValue(scope, out var entry) && entry.Paused;
    }

    public void ThrowIfBlocked(string scope)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(scope, out var entry)) return;
            if (entry.Paused) throw new SipRegistrationRetryBlockedException("IMS automatic registration retries are paused by registrar feedback; explicit retry is required.", null, true);
            if (Remaining(entry) > TimeSpan.Zero)
                throw new SipRegistrationRetryBlockedException("IMS registration is waiting for the registrar Retry-After interval.", _time.GetUtcNow().UtcDateTime.Add(Remaining(entry)), false);
            _entries.Remove(scope);
        }
    }

    public async Task WaitUntilAllowedAsync(string scope, CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan remaining;
            lock (_sync)
            {
                if (!_entries.TryGetValue(scope, out var entry)) return;
                if (entry.Paused) throw new SipRegistrationRetryBlockedException("IMS automatic registration retries are paused by registrar feedback; explicit retry is required.", null, true);
                remaining = Remaining(entry);
                if (remaining <= TimeSpan.Zero) { _entries.Remove(scope); return; }
            }
            await Task.Delay(remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining, _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>An explicit user retry can release a rejection pause, but never shorten a valid server timer.</summary>
    public void ReleasePause(string scope)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(scope, out var entry) && entry.Paused)
                _entries[scope] = entry with { Paused = false };
        }
    }

    private TimeSpan Remaining(Entry entry) => entry.Delay - _time.GetElapsedTime(entry.Started);
}
