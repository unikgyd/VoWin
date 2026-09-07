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

    public Ec25AkaProvider(IAtSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    public async Task<bool> CheckReadyAsync(string? expectedIccid = null, CancellationToken ct = default)
    {
        if (!_session.IsOpen) return false;

        var cpin = await _session.ExecuteCommandAsync("AT+CPIN?", 2000, ct).ConfigureAwait(false);
        if (!cpin.Success) return false;
        if (!Regex.IsMatch(cpin.RawOutput ?? "", @"\+CPIN:\s*READY", RegexOptions.IgnoreCase))
            return false;

        // A card that cannot report its ICCID is not usable for AKA.
        var iccid = await ReadIccidAsync(ct).ConfigureAwait(false);
        if (iccid is null) return false;

        if (string.IsNullOrWhiteSpace(expectedIccid)) return true;

        return string.Equals(NormalizeIccid(iccid), NormalizeIccid(expectedIccid), StringComparison.Ordinal);
    }

    public async Task<AkaResult> AuthenticateAsync(AkaChallenge challenge, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);

        var apdu = HardwareAka.BuildAuthenticateApdu(challenge.Rand, challenge.Autn);

        var channelId = await OpenUsimChannelAsync(ct).ConfigureAwait(false);
        byte[]? response;

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
            response = await ExchangeAsync(apdu, channelId: 0, ct).ConfigureAwait(false);
        }

        if (response is null || response.Length == 0)
        {
            Console.WriteLine("[Ec25AkaProvider] Modem returned no APDU response for USIM AUTHENTICATE.");
            return AkaResult.Failed("Modem returned no APDU response for USIM AUTHENTICATE.");
        }

        Console.WriteLine($"[Ec25AkaProvider] APDU Response: {Convert.ToHexString(response)}");
        var parsed = HardwareAka.ParseAuthenticateResponse(response).ToAkaResult();
        Console.WriteLine($"[Ec25AkaProvider] Parsed Result: Success={parsed.Success}, SyncFail={parsed.SynchronizationFailure}, Err={parsed.ErrorMessage}");
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

    private async Task<int> OpenUsimChannelAsync(CancellationToken ct)
    {
        foreach (var aid in UsimAids)
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

    /// <summary>Matches <c>+QCCID</c>, <c>+CCID</c> and <c>+ICCID</c> responses.</summary>
    private static readonly Regex IccidPattern =
        new(@"\+[A-Z]*CCID\s*:?\s*([0-9A-Fa-f]{18,20})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
}
