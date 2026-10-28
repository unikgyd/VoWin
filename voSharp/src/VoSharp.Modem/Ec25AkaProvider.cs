using System.Text.RegularExpressions;
using VoSharp.Common.Aka;
using VoSharp.Common.Utils;
using VoSharp.Modem.At;
using VoSharp.Sim;

namespace VoSharp.Modem;

/// <summary>
/// UMTS AKA provider backed by a Quectel EC25 (and compatible) cellular module.
/// </summary>
/// <remarks>
/// Transport strategy, in order of preference:
/// <list type="number">
///   <item><description>
///     <c>AT+CCHO</c> opens a logical channel on ADF.USIM, then <c>AT+CGLA</c> exchanges the APDU.
///     Required for UICCs carrying multiple applications — the CLA byte must be rewritten to the
///     allocated channel id.
///   </description></item>
///   <item><description><c>AT+CSIM</c> generic APDU pass-through (fallback).</description></item>
/// </list>
/// Both paths follow ISO 7816-4 GET RESPONSE chaining: when the card answers <c>61 xx</c> or
/// <c>9F xx</c> more data is pending and we must issue <c>CLA C0 00 00 &lt;len&gt;</c> until a
/// final status word arrives. Ported from <c>voCore/internal/sim/hardware_aka.go</c>.
/// </remarks>
public sealed class Ec25AkaProvider : IAkaProvider
{
    /// <summary>ADF.USIM application identifiers, most specific first.</summary>
    private static readonly string[] UsimAids =
    {
        "A0000000871002",                  // USIM (3GPP TS 31.102)
        "A0000000871002FF86FF0389FFFFFFFF" // USIM with full AID suffix
    };

    private readonly IAtSession _session;
    private string? _selectedBasicUsimAid;

    public Ec25AkaProvider(IAtSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    public async Task<bool> CheckReadyAsync(string? expectedIccid = null, CancellationToken ct = default)
    {
        var result = await VerifyUsimReadyAsync(expectedIccid, requireCardIccid: false, ct: ct).ConfigureAwait(false);
        return result.Ready;
    }

    /// <summary>Restores the basic-channel USIM selection after read-only eUICC inventory.
    /// Some EC20 firmware loses its baseband USIM view if ISD-R probing is not followed
    /// immediately by this selection. No AKA, PIN, REFRESH or profile change is sent.
    /// This is cleanup, not a replacement for the next ICCID/CPIN readiness check.</summary>
    public async Task<bool> RestoreBasicUsimApplicationAsync(CancellationToken ct = default)
    {
        if (!_session.IsOpen) return false;
        var candidates = _selectedBasicUsimAid is { } aid ? new[] { aid }.Concat(UsimAids) : UsimAids;
        return await SelectBasicUsimApplicationAsync(candidates.Distinct(StringComparer.OrdinalIgnoreCase), ct)
            .ConfigureAwait(false) is not null;
    }

    /// <summary>
    /// Verifies that a usable USIM is present: CPIN is ready, ICCID identifies
    /// the expected card, and ADF.USIM can be selected. For profile switching,
    /// callers set <paramref name="requireCardIccid"/> so cached QCCID/CCID is
    /// never accepted. Ordinary startup permits that fallback for firmware
    /// which does not expose EF.ICCID through CSIM.
    /// </summary>
    public async Task<(bool Ready, string? CardIccid, string? Failure, bool UsedCardIccid)> VerifyUsimReadyAsync(
        string? expectedIccid,
        bool requireCardIccid,
        CancellationToken ct = default)
    {
        if (!await IsPinReadyAsync(ct).ConfigureAwait(false))
            return (false, null, "CPIN is not READY", false);

        var cardIccid = await ReadIccidFromCardAsync(ct).ConfigureAwait(false);
        var usedCardIccid = cardIccid is not null;
        if (cardIccid is null && !requireCardIccid)
            cardIccid = await ReadIccidAsync(ct).ConfigureAwait(false);
        if (cardIccid is null)
            return (false, null,
                requireCardIccid
                    ? "EF.ICCID could not be read from the active card"
                    : "Neither EF.ICCID nor the modem ICCID view could be read",
                false);

        if (!string.IsNullOrWhiteSpace(expectedIccid) &&
            !string.Equals(NormalizeIccid(cardIccid), NormalizeIccid(expectedIccid), StringComparison.Ordinal))
        {
            return (false, cardIccid,
                usedCardIccid
                    ? "EF.ICCID does not match the expected SIM identity"
                    : "The modem ICCID view does not match the expected SIM identity",
                usedCardIccid);
        }

        var selectedAid = await SelectBasicUsimApplicationAsync(
            await DiscoverUsimAidsAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        return selectedAid is null
            ? (false, cardIccid, "ADF.USIM could not be selected on the basic CSIM channel", usedCardIccid)
            : (true, cardIccid, null, usedCardIccid);
    }

    /// <summary>
    /// Performs the strict post-eSIM-switch readiness check. The card's own
    /// EF.ICCID is mandatory; the baseband ICCID cache is never accepted.
    /// </summary>
    public async Task<(bool Ready, string? CardIccid, string? Failure, bool UsedCardIccid)> VerifyPostProfileSwitchReadyAsync(
        string? expectedIccid,
        CancellationToken ct = default)
    {
        return await VerifyUsimReadyAsync(expectedIccid, requireCardIccid: true, ct: ct).ConfigureAwait(false);
    }

    public async Task<AkaResult> AuthenticateAsync(AkaChallenge challenge, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);

        var apdu = HardwareAka.BuildAuthenticateApdu(challenge.Rand, challenge.Autn);

        var aids = await DiscoverUsimAidsAsync(ct).ConfigureAwait(false);
        var channelId = await OpenUsimChannelAsync(aids, ct).ConfigureAwait(false);
        byte[]? response;
        string? basicChannelAid = null;

        if (channelId > 0)
        {
            try
            {
                response = await ExchangeAsync(apdu, channelId, ct).ConfigureAwait(false);
            }
            finally
            {
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _session.ExecuteCommandAsync($"AT+CCHC={channelId}", 2000, cleanupCts.Token)
                              .ConfigureAwait(false);
            }
        }
        else
        {
            // CCHO may be unavailable immediately after an eSIM profile switch.
            // Basic-channel CSIM does not inherit an ADF.USIM selection, so select
            // the app explicitly before AUTHENTICATE. Without this, many eUICCs
            // answer 6985 even though CPIN and ICCID both look healthy.
            basicChannelAid = await SelectBasicUsimApplicationAsync(aids, ct).ConfigureAwait(false);
            if (basicChannelAid is null)
                return AkaResult.Failed("Unable to select ADF.USIM on the basic CSIM channel before AUTHENTICATE.");
            response = await ExchangeAsync(apdu, channelId: 0, ct).ConfigureAwait(false);
        }

        if (response is null || response.Length == 0)
        {
            Console.WriteLine("[Ec25AkaProvider] Modem returned no APDU response for USIM AUTHENTICATE.");
            return AkaResult.Failed("Modem returned no APDU response for USIM AUTHENTICATE.");
        }

        // Never log the raw AUTHENTICATE APDU response: a successful response contains RES, CK
        // and IK, and a synchronization response contains AUTS. Only status and lengths are safe.
        var parsedResponse = HardwareAka.ParseAuthenticateResponse(response);
        // Some eUICCs accept CCHO but reject AUTHENTICATE on that logical channel
        // with 6985.  The same applet can still accept the basic-channel CSIM
        // exchange.  The command was not executed, so this one safe fallback
        // does not consume a second AKA vector.
        var usedBasicChannelFallback = false;
        if (channelId > 0 && IsConditionsNotSatisfied(response))
        {
            Console.WriteLine("[Ec25AkaProvider] Logical-channel USIM AUTHENTICATE returned SW=6985; retrying once through basic-channel CSIM.");
            usedBasicChannelFallback = true;
            basicChannelAid = await SelectBasicUsimApplicationAsync(aids, ct).ConfigureAwait(false);
            if (basicChannelAid is null)
                return AkaResult.Failed("Logical-channel AUTHENTICATE returned SW=6985 and ADF.USIM could not be selected on the basic CSIM channel.");
            response = await ExchangeAsync(apdu, channelId: 0, ct).ConfigureAwait(false);
            if (response is null || response.Length == 0)
                return AkaResult.Failed("USIM AUTHENTICATE basic-channel fallback returned no APDU response.");
            parsedResponse = HardwareAka.ParseAuthenticateResponse(response);
        }

        var transportNote = usedBasicChannelFallback
            ? "Logical-channel AUTHENTICATE returned SW=6985; basic-channel CSIM fallback was used."
            : basicChannelAid is not null
                ? $"Logical channel was unavailable; selected ADF.USIM ({MaskAid(basicChannelAid)}) on the basic CSIM channel."
                : null;
        var parsed = parsedResponse.Success
            ? AkaResult.Succeeded(parsedResponse.Res!, parsedResponse.Ck!, parsedResponse.Ik!, transportNote)
            : parsedResponse.SyncFailure
                ? AkaResult.SyncFailed(parsedResponse.Auts!, transportNote)
                : AkaResult.Failed(
                    usedBasicChannelFallback
                        ? $"Basic-channel CSIM fallback failed after logical-channel SW=6985: {parsedResponse.ErrorMessage}"
                        : parsedResponse.ErrorMessage ?? "USIM AUTHENTICATE failed.",
                    transportNote);
        Console.WriteLine($"[Ec25AkaProvider] USIM AUTHENTICATE completed: bytes={response.Length}, Success={parsed.Success}, SyncFail={parsed.SynchronizationFailure}, Err={parsed.ErrorMessage}");
        return parsed;
    }

    /// <summary>Reads the card's ICCID, trying the vendor-specific commands in turn.</summary>
    public async Task<string?> ReadIccidAsync(CancellationToken ct = default)
    {
        foreach (var command in new[] { "AT+QCCID", "AT+CCID", "AT+ICCID" })
        {
            var reply = await _session.ExecuteCommandAsync(command, 3000, ct).ConfigureAwait(false);
            if (!reply.Success) continue;

            var match = IccidPattern.Match(reply.RawOutput ?? "");
            if (match.Success) return match.Groups[1].Value;
        }

        return null;
    }

    /// <summary>
    /// Normalises an EF-ICCID read into the digit order printed on the card: strips separators and
    /// un-swaps the nibble pairs as they are stored on the SIM.
    /// </summary>
    public static string NormalizeIccid(string iccid)
    {
        var digits = new string(iccid.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length < 18) return digits;

        var swapped = new char[digits.Length];
        for (var i = 0; i + 1 < digits.Length; i += 2)
        {
            swapped[i] = digits[i + 1];
            swapped[i + 1] = digits[i];
        }
        if (digits.Length % 2 != 0)
            swapped[^1] = digits[^1];

        var result = new string(swapped).TrimEnd('F', 'f');
        return result.Length >= 18 ? result : digits;
    }

    /// <summary>
    /// Sends an APDU and follows GET RESPONSE chaining. Returns the reassembled response with the
    /// final status word appended, matching what <see cref="HardwareAka.ParseAuthenticateResponse"/>
    /// expects.
    /// </summary>
    /// <param name="channelId">0 to use AT+CSIM; otherwise the AT+CCHO logical channel.</param>
    private async Task<byte[]?> ExchangeAsync(byte[] apdu, int channelId, CancellationToken ct)
    {
        var cla = channelId > 0 ? (byte)channelId : (byte)0x00;

        // The CLA byte carries the logical channel number on every exchange, including the
        // initial AUTHENTICATE — not just the GET RESPONSE follow-ups.
        var current = (byte[])apdu.Clone();
        current[0] = cla;

        return await HardwareAka.TransmitWithChainingAsync(
            async (command, token) =>
            {
                var hex = HexUtils.ToHexString(command);
                var atCommand = channelId > 0
                    ? $"AT+CGLA={channelId},{hex.Length},\"{hex}\""
                    : $"AT+CSIM={hex.Length},\"{hex}\"";

                var resp = await _session.ExecuteCommandAsync(atCommand, 4000, token)
                                         .ConfigureAwait(false);
                return resp.Success
                    ? ExtractResponseBytes(resp, channelId > 0 ? "+CGLA:" : "+CSIM:")
                    : null;
            },
            current,
            cla,
            ct).ConfigureAwait(false);
    }

    private async Task<int> OpenUsimChannelAsync(IEnumerable<string> aids, CancellationToken ct)
    {
        foreach (var aid in aids)
        {
            var resp = await _session.ExecuteCommandAsync($"AT+CCHO=\"{aid}\"", 2000, ct)
                                     .ConfigureAwait(false);
            if (!resp.Success) continue;

            var match = CchoPattern.Match(resp.RawOutput ?? "");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var id) && id > 0)
                return id;
        }
        return 0;
    }

    private async Task<string?> SelectBasicUsimApplicationAsync(IEnumerable<string> aids, CancellationToken ct)
    {
        foreach (var aid in aids)
        {
            var aidBytes = HexUtils.FromHexString(aid);
            var select = new byte[5 + aidBytes.Length];
            select[0] = 0x00;
            select[1] = 0xA4;
            select[2] = 0x04;
            select[3] = 0x04; // Select by DF name and request FCP.
            select[4] = checked((byte)aidBytes.Length);
            aidBytes.CopyTo(select, 5);

            var response = await ExchangeAsync(select, channelId: 0, ct).ConfigureAwait(false);
            if (response is { Length: >= 2 } && response[^2] == 0x90 && response[^1] == 0x00)
            {
                _selectedBasicUsimAid = aid;
                return aid;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads EF.ICCID (MF/2FE2) through AT+CSIM and decodes its swapped-nibble
    /// BCD payload. This is the active card's answer, rather than the modem's
    /// cached +QCCID/+CCID view. The basic channel is returned to MF afterwards
    /// so subsequent baseband operations do not inherit EF.ICCID's selection.
    /// </summary>
    public async Task<string?> ReadIccidFromCardAsync(CancellationToken ct = default)
    {
        if (!_session.IsOpen) return null;

        try
        {
            var selected = false;
            foreach (var select in new[]
            {
                HexUtils.FromHexString("00A4080C022FE2"),
                HexUtils.FromHexString("00A40804022FE2")
            })
            {
                var selectResponse = await ExchangeAsync(select, channelId: 0, ct).ConfigureAwait(false);
                if (HasSuccessStatus(selectResponse))
                {
                    selected = true;
                    break;
                }
            }

            if (!selected) return null;

            var response = await ExchangeAsync(HexUtils.FromHexString("00B000000A"), channelId: 0, ct)
                .ConfigureAwait(false);
            if (response is not { Length: 12 } || !HasSuccessStatus(response))
                return null;

            var iccid = DecodeEfIccid(response.AsSpan(0, 10));
            return iccid.Length == 0 ? null : iccid;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            // The cancellation token which stopped an eSIM operation must not
            // prevent best-effort restoration of the basic-channel file state.
            using var restoreCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await ExchangeAsync(HexUtils.FromHexString("00A40004023F00"), channelId: 0, restoreCts.Token)
                    .ConfigureAwait(false);
            }
            catch { }
        }
    }

    /// <summary>Decodes the ten-byte swapped-nibble BCD payload of EF.ICCID.</summary>
    public static string DecodeEfIccid(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 10) return string.Empty;

        Span<char> digits = stackalloc char[20];
        var length = 0;
        var padding = false;
        foreach (var value in bytes)
        {
            for (var shift = 0; shift <= 4; shift += 4)
            {
                var nibble = (value >> shift) & 15;
                if (nibble == 15) { padding = true; continue; }
                // F is trailing padding, not a separator to be silently removed.
                if (padding || nibble > 9) return string.Empty;
                digits[length++] = (char)('0' + nibble);
            }
        }

        return length is >= 18 and <= 20 && digits[0] == '8' && digits[1] == '9'
            ? new string(digits[..length])
            : string.Empty;
    }

    private async Task<bool> IsPinReadyAsync(CancellationToken ct)
    {
        if (!_session.IsOpen) return false;

        var cpin = await _session.ExecuteCommandAsync("AT+CPIN?", 2000, ct).ConfigureAwait(false);
        return cpin.Success && Regex.IsMatch(cpin.RawOutput ?? "", @"\+CPIN:\s*READY", RegexOptions.IgnoreCase);
    }

    private static bool HasSuccessStatus(byte[]? response) =>
        response is { Length: >= 2 } && response[^2] == 0x90 && response[^1] == 0x00;

    private async Task<IReadOnlyList<string>> DiscoverUsimAidsAsync(CancellationToken ct)
    {
        var candidates = new List<string>();
        try
        {
            var response = await _session.ExecuteCommandAsync("AT+CUAD", 2000, ct).ConfigureAwait(false);
            if (response.Success)
            {
                var text = response.RawOutput ?? string.Empty;
                foreach (Match match in UsimAidInDirectoryPattern.Matches(text))
                {
                    var length = Convert.ToInt32(match.Groups[1].Value, 16);
                    var aid = match.Groups[2].Value;
                    if (length == aid.Length / 2 && aid.Length % 2 == 0)
                        candidates.Add(aid.ToUpperInvariant());
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // CUAD is optional; use the standard AIDs below when a modem does not implement it.
        }

        candidates.AddRange(UsimAids);
        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(aid => aid.Length)
            .ToArray();
    }

    private static string MaskAid(string aid) => aid.Length <= 14 ? aid : aid[..14] + "…";

    /// <summary>
    /// Pulls the hex APDU response out of a <c>+CGLA:</c> / <c>+CSIM:</c> line.
    /// </summary>
    /// <remarks>
    /// Modems disagree on whether a length field precedes the quoted payload: Quectel emits
    /// <c>+CGLA: 1,32,"DB08...9000"</c>, others emit <c>+CGLA: 1,"DB08...9000"</c>. A regex such as
    /// <c>\+CGLA:\s*\d+,\s*"?([0-9A-Fa-f]+)"?</c> matches only the length field in the first form,
    /// silently yielding a two-byte "response". Prefer the last quoted token and fall back to the
    /// trailing comma field when the payload is unquoted.
    /// </remarks>
    public static byte[]? ExtractResponseBytes(AtResponse response, string prefix)
    {
        foreach (var line in response.Lines)
        {
            if (!line.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var firstQuote = line.IndexOf('"');
            var lastQuote = line.LastIndexOf('"');
            var payload = firstQuote >= 0 && lastQuote > firstQuote
                ? line[(firstQuote + 1)..lastQuote]
                : line[(line.LastIndexOf(',') + 1)..].Trim();

            payload = Regex.Replace(payload, @"\s+", "");
            if (payload.Length == 0)
                return null;

            try
            {
                return HexUtils.FromHexString(payload);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        return null;
    }

    private static readonly Regex CchoPattern =
        new(@"\+CCHO:\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex UsimAidInDirectoryPattern =
        new(@"4F([0-9A-Fa-f]{2})(A0000000871002[0-9A-Fa-f]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static bool IsConditionsNotSatisfied(byte[] response) =>
        response.Length >= 2 && response[^2] == 0x69 && response[^1] == 0x85;

    /// <summary>Matches <c>+QCCID</c>, <c>+CCID</c> and <c>+ICCID</c> responses.</summary>
    private static readonly Regex IccidPattern =
        new(@"\+[A-Z]*CCID\s*:?\s*([0-9A-Fa-f]{18,20})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
}
