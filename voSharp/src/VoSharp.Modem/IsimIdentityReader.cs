using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using VoSharp.Common.Aka;
using VoSharp.Modem.At;
using VoSharp.Sim;

namespace VoSharp.Modem;

public enum IsimIdentityReadiness
{
    Available,
    SimUnavailable,
    ApplicationAbsent,
    ApplicationUnavailable,
    ReadFailed
}

/// <summary>ISIM identities read directly from the selected UICC application.</summary>
public sealed record IsimIdentity(string PrivateIdentity, string HomeDomain, string DefaultPublicIdentity);

public sealed record IsimIdentityProbeResult(IsimIdentityReadiness Readiness, IsimIdentity? Identity, string Summary);

internal readonly record struct IsimApplicationDirectory(bool Readable, IReadOnlyList<string> Aids);

internal interface IIsimApduTransport
{
    bool IsOpen { get; }
    Task<(bool Ready, bool Absent)> CheckReadyAsync(CancellationToken ct);
    Task<IsimApplicationDirectory> GetApplicationsAsync(CancellationToken ct);
    Task<int> OpenApplicationAsync(string aid, CancellationToken ct);
    Task<byte[]?> TransmitAsync(int channelId, byte[] command, CancellationToken ct);
    Task CloseApplicationAsync(int channelId, CancellationToken ct);
}

/// <summary>
/// Read-only 3GPP TS 31.103 EF.IMPI/EF.DOMAIN/first EF.IMPU reader. Never
/// substitutes a guessed IMSI-derived identity for an unreadable ISIM.
/// </summary>
public sealed class IsimIdentityReader
{
    private const string IsimAid = "A0000000871004";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IIsimApduTransport _transport;

    public IsimIdentityReader(IAtSession session) : this(new AtIsimApduTransport(session)) { }

    internal IsimIdentityReader(IIsimApduTransport transport) =>
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public async Task<IsimIdentityProbeResult> ReadAsync(CancellationToken ct = default)
    {
        if (!_transport.IsOpen)
            return new(IsimIdentityReadiness.ReadFailed, null, "UICC channel is not open.");

        var pin = await _transport.CheckReadyAsync(ct).ConfigureAwait(false);
        if (!pin.Ready)
        {
            return pin.Absent
                ? new(IsimIdentityReadiness.SimUnavailable, null, "No selected SIM is present.")
                : new(IsimIdentityReadiness.ReadFailed, null, "Selected SIM is not PIN-ready.");
        }

        var opened = await OpenIsimChannelAsync(ct).ConfigureAwait(false);
        var channelId = opened.ChannelId;
        if (channelId == 0)
            return opened.IsimAbsent
                ? new(IsimIdentityReadiness.ApplicationAbsent, null,
                    "UICC application directory lists no ISIM and ADF.ISIM could not be opened.")
                : new(IsimIdentityReadiness.ApplicationUnavailable, null,
                    "ADF.ISIM could not be opened on a logical UICC channel.");

        try
        {
            var impi = await ReadIdentityFileAsync(channelId, 0x02, record: false, ct).ConfigureAwait(false);
            var domain = await ReadIdentityFileAsync(channelId, 0x03, record: false, ct).ConfigureAwait(false);
            var impu = await ReadIdentityFileAsync(channelId, 0x04, record: true, ct).ConfigureAwait(false);
            if (!IsValidPrivateIdentity(impi) || !IsValidDomain(domain) || !IsValidPublicIdentity(impu))
                return new(IsimIdentityReadiness.ReadFailed, null,
                    "ADF.ISIM opened, but its mandatory IMS identity files were missing or invalid.");
            return new(IsimIdentityReadiness.Available, new(impi!, domain!, impu!),
                "ISIM private identity, home domain and default public identity were read from the card.");
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _transport.CloseApplicationAsync(channelId, cleanup.Token).ConfigureAwait(false); }
            catch { /* Best-effort channel cleanup; preserve the read result or original error. */ }
        }
    }

    /// <summary>Runs IMS AKA on ADF.ISIM, not on a potentially different USIM key.</summary>
    public async Task<AkaResult> AuthenticateAsync(AkaChallenge challenge, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        if (!_transport.IsOpen) return AkaResult.Failed("UICC channel is not open.");
        var channelId = (await OpenIsimChannelAsync(ct).ConfigureAwait(false)).ChannelId;
        if (channelId == 0) return AkaResult.Failed("ADF.ISIM is unavailable for IMS AKA.");

        try
        {
            var apdu = HardwareAka.BuildAuthenticateApdu(challenge.Rand, challenge.Autn);
            apdu[0] = ChannelClassByte(channelId);
            byte[]? response;
            try { response = await ExchangeAsync(channelId, apdu, ct).ConfigureAwait(false); }
            finally { CryptographicOperations.ZeroMemory(apdu); }
            if (response is null) return AkaResult.Failed("ISIM AUTHENTICATE returned no APDU response.");
            try
            {
                var parsed = HardwareAka.ParseAuthenticateResponse(response);
                return parsed.Success
                    ? AkaResult.Succeeded(parsed.Res!, parsed.Ck!, parsed.Ik!)
                    : parsed.SyncFailure
                        ? AkaResult.SyncFailed(parsed.Auts!)
                        : AkaResult.Failed(parsed.ErrorMessage ?? "ISIM AUTHENTICATE failed.");
            }
            finally { CryptographicOperations.ZeroMemory(response); }
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _transport.CloseApplicationAsync(channelId, cleanup.Token).ConfigureAwait(false); }
            catch { }
        }
    }

    private async Task<(int ChannelId, bool IsimAbsent)> OpenIsimChannelAsync(CancellationToken ct)
    {
        var aids = new List<string>();
        IsimApplicationDirectory directory;
        try { directory = await _transport.GetApplicationsAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            directory = new IsimApplicationDirectory(false, []);
        }
        var directoryHasApplications = directory.Readable && directory.Aids.Any(aid =>
            aid.Length >= 14 && aid.StartsWith("A00000008710", StringComparison.OrdinalIgnoreCase));
        aids.AddRange(directory.Aids.Where(aid =>
            aid.StartsWith(IsimAid, StringComparison.OrdinalIgnoreCase)));
        aids.Add(IsimAid);

        foreach (var aid in aids.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(aid => aid.Length))
        {
            var channelId = await _transport.OpenApplicationAsync(aid, ct).ConfigureAwait(false);
            if (channelId < 1) continue;
            if (channelId <= 19) return (channelId, false);

            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _transport.CloseApplicationAsync(channelId, cleanup.Token).ConfigureAwait(false); }
            catch { }
        }
        // Only a readable application directory with another valid AID can
        // distinguish an absent ISIM from a transient AT/CCHO failure.
        return (0, directoryHasApplications && aids.Count == 1);
    }

    private async Task<string?> ReadIdentityFileAsync(int channelId, byte fileIdLow, bool record, CancellationToken ct)
    {
        var cla = ChannelClassByte(channelId);
        var select = new byte[] { cla, 0xA4, 0x00, 0x04, 0x02, 0x6F, fileIdLow };
        var selected = await ExchangeAsync(channelId, select, ct).ConfigureAwait(false);
        if (!HasSuccessStatus(selected))
        {
            select[3] = 0x0C; // Some cards support select-without-response only.
            selected = await ExchangeAsync(channelId, select, ct).ConfigureAwait(false);
            if (!HasSuccessStatus(selected)) return null;
        }

        var read = record
            ? new byte[] { cla, 0xB2, 0x01, 0x04, 0x00 } // first/default EF.IMPU record
            : new byte[] { cla, 0xB0, 0x00, 0x00, 0x00 }; // up to 256 bytes
        var response = await ExchangeAsync(channelId, read, ct).ConfigureAwait(false);
        if (response is { Length: 2 } && response[0] == 0x6C)
        {
            read[^1] = response[1];
            response = await ExchangeAsync(channelId, read, ct).ConfigureAwait(false);
        }
        return HasSuccessStatus(response) ? DecodeIdentityTlv(response.AsSpan(0, response!.Length - 2)) : null;
    }

    private async Task<byte[]?> ExchangeAsync(int channelId, byte[] apdu, CancellationToken ct)
    {
        var cla = ChannelClassByte(channelId);
        return await HardwareAka.TransmitWithChainingAsync(
            (command, token) => _transport.TransmitAsync(channelId, command, token),
            apdu, cla, ct).ConfigureAwait(false);
    }

    private static byte ChannelClassByte(int channelId) => channelId <= 3
        ? checked((byte)channelId)
        : checked((byte)(0x40 | (channelId - 4)));

    private static bool HasSuccessStatus(byte[]? response) =>
        response is { Length: >= 2 } && response[^2] == 0x90 && response[^1] == 0x00;

    internal static string? DecodeIdentityTlv(ReadOnlySpan<byte> fileData)
    {
        if (fileData.Length < 2 || fileData[0] != 0x80) return null;
        var offset = 2;
        var length = (int)fileData[1];
        if (length == 0x81)
        {
            if (fileData.Length < 3) return null;
            length = fileData[2];
            offset = 3;
        }
        else if (length == 0x82)
        {
            if (fileData.Length < 4) return null;
            length = (fileData[2] << 8) | fileData[3];
            offset = 4;
        }
        else if (length > 0x7F) return null;
        if (length == 0 || length > fileData.Length - offset) return null;
        try
        {
            var value = StrictUtf8.GetString(fileData.Slice(offset, length)).Trim();
            return value.Length > 0 && !value.Any(char.IsControl) ? value : null;
        }
        catch (DecoderFallbackException) { return null; }
    }

    private static bool IsValidPrivateIdentity(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains('@') && !value.Any(char.IsWhiteSpace);
    private static bool IsValidDomain(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains('.') && !value.Contains('@') && !value.Any(char.IsWhiteSpace);
    private static bool IsValidPublicIdentity(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) &&
        !value.Any(char.IsWhiteSpace);
}

internal sealed class AtIsimApduTransport : IIsimApduTransport
{
    private static readonly Regex CchoPattern = new(@"\+CCHO:\s*(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DirectoryAidTagPattern = new(@"4F([0-9A-Fa-f]{2})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly IAtSession _session;

    public AtIsimApduTransport(IAtSession session) =>
        _session = session ?? throw new ArgumentNullException(nameof(session));

    public bool IsOpen => _session.IsOpen;

    public async Task<(bool Ready, bool Absent)> CheckReadyAsync(CancellationToken ct)
    {
        var pin = await _session.ExecuteCommandAsync("AT+CPIN?", 3000, ct).ConfigureAwait(false);
        return (pin.Success && pin.Lines.Any(line =>
                line.Contains("+CPIN: READY", StringComparison.OrdinalIgnoreCase)),
            pin.ErrorCode?.Contains("CME ERROR: 10", StringComparison.OrdinalIgnoreCase) == true);
    }

    public async Task<IsimApplicationDirectory> GetApplicationsAsync(CancellationToken ct)
    {
        var directory = await _session.ExecuteCommandAsync("AT+CUAD", 3000, ct).ConfigureAwait(false);
        if (!directory.Success) return new(false, []);
        var aids = new List<string>();
        var raw = directory.RawOutput ?? string.Join('\n', directory.Lines);
        foreach (var line in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains("+CUAD:", StringComparison.OrdinalIgnoreCase)) continue;
            var firstQuote = line.IndexOf('"');
            var lastQuote = line.LastIndexOf('"');
            var hex = firstQuote >= 0 && lastQuote > firstQuote
                ? line[(firstQuote + 1)..lastQuote]
                : line[(line.IndexOf(':') + 1)..].Trim();
            foreach (Match match in DirectoryAidTagPattern.Matches(hex))
            {
                if (!int.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var bytes)) continue;
                var start = match.Index + match.Length;
                if (bytes < 7 || start + bytes * 2 > hex.Length) continue;
                var aid = hex.Substring(start, bytes * 2);
                if (aid.All(Uri.IsHexDigit) &&
                    aid.StartsWith("A00000008710", StringComparison.OrdinalIgnoreCase))
                    aids.Add(aid.ToUpperInvariant());
            }
        }
        return new(aids.Count > 0, aids);
    }

    public async Task<int> OpenApplicationAsync(string aid, CancellationToken ct)
    {
        var opened = await _session.ExecuteCommandAsync($"AT+CCHO=\"{aid}\"", 3000, ct).ConfigureAwait(false);
        var match = CchoPattern.Match(opened.RawOutput ?? string.Join('\n', opened.Lines));
        return opened.Success && match.Success && int.TryParse(match.Groups[1].Value, out var channelId)
            ? channelId : 0;
    }

    public async Task<byte[]?> TransmitAsync(int channelId, byte[] command, CancellationToken ct)
    {
        var hex = Convert.ToHexString(command);
        var response = await _session.ExecuteCommandAsync(
            $"AT+CGLA={channelId},{hex.Length},\"{hex}\"", 4000, ct).ConfigureAwait(false);
        return response.Success ? Ec25AkaProvider.ExtractResponseBytes(response, "+CGLA:") : null;
    }

    public async Task CloseApplicationAsync(int channelId, CancellationToken ct)
    {
        await _session.ExecuteCommandAsync($"AT+CCHC={channelId}", 2000, ct).ConfigureAwait(false);
    }
}
