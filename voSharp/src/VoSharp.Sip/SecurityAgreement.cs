using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VoSharp.Sip;

/// <summary>
/// One ipsec-3gpp security mechanism offered to (or selected by) the P-CSCF.
/// 3GPP TS 33.203 §7.2 / RFC 3329.
/// </summary>
/// <param name="SpiClient">SPI of the SA the UE uses when sending (UE client port → P-CSCF server port).</param>
/// <param name="SpiServer">SPI of the SA the P-CSCF uses when sending to the UE server port.</param>
/// <param name="PortClient">UE-side source port used when the UE dials out.</param>
/// <param name="PortServer">UE-side destination port the P-CSCF dials back into; must be listening.</param>
public sealed record SecurityProposal(
    string IntegrityAlgorithm,
    string EncryptionAlgorithm,
    uint SpiClient,
    uint SpiServer,
    int PortClient,
    int PortServer);

/// <summary>
/// The mechanism the P-CSCF chose, plus the exact header value to echo back in
/// <c>Security-Verify</c>.
/// </summary>
public sealed record SecurityAgreement(
    SecurityProposal Selected,
    string VerifyValue,
    uint PcscfClientSpi,
    uint PcscfServerSpi,
    int PcscfClientPort,
    int PcscfServerPort);

/// <summary>
/// Security agreement (sec-agree) for IMS registration.
/// </summary>
/// <remarks>
/// Port naming is the usual source of confusion: <c>port-c</c>/<c>spi-c</c> describe the SA the
/// <b>UE</b> uses as a client (outbound, from <c>port-c</c>), while <c>port-s</c>/<c>spi-s</c>
/// describe the SA on the port the <b>P-CSCF</b> connects back to. Both are UE-side values in
/// Security-Client; the P-CSCF answers with its own pair.
/// </remarks>
public static class SecurityAgreementBuilder
{
    /// <summary>Integrity algorithms offered, in preference order.</summary>
    public static readonly string[] DefaultIntegrityAlgorithms = { "hmac-sha-1-96", "hmac-md5-96" };

    /// <summary>Encryption algorithms offered, in preference order.</summary>
    public static readonly string[] DefaultEncryptionAlgorithms = { "aes-cbc", "des-ede3-cbc", "null" };

    private const string MechanismName = "ipsec-3gpp";

    /// <summary>
    /// Builds the <c>Security-Client</c> value using the default algorithm sets.
    /// </summary>
    public static string BuildSecurityClient(SecurityProposal proposal) =>
        BuildSecurityClient(proposal, DefaultIntegrityAlgorithms, DefaultEncryptionAlgorithms);

    /// <summary>
    /// Builds the <c>Security-Client</c> value: one ipsec-3gpp mechanism per
    /// integrity × encryption combination, ordered by descending q.
    /// Some carriers (for example O2 DE) only accept a single integrity-only offer,
    /// so the algorithm sets are caller-selectable.
    /// </summary>
    public static string BuildSecurityClient(
        SecurityProposal proposal,
        IReadOnlyList<string> integrityAlgorithms,
        IReadOnlyList<string> encryptionAlgorithms)
    {
        var offers = new List<string>();
        var index = 0;

        foreach (var integrity in integrityAlgorithms)
        {
            foreach (var encryption in encryptionAlgorithms)
            {
                // Mirrors the reference implementation: the first offer is 1.000 and the rest
                // descend from 0.998. Only the relative order matters to the P-CSCF.
                var preference = index == 0 ? "1.000" : string.Format(CultureInfo.InvariantCulture, "0.{0:D3}", 999 - index);

                offers.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0};q={1};alg={2};prot=esp;mod=trans;ealg={3};spi-c={4};spi-s={5};port-c={6};port-s={7}",
                    MechanismName,
                    preference,
                    integrity,
                    encryption,
                    FormatSpi(proposal.SpiClient),
                    FormatSpi(proposal.SpiServer),
                    proposal.PortClient,
                    proposal.PortServer));

                index++;
            }
        }

        return string.Join(", ", offers);
    }

    /// <summary>TS 33.203 requires SPIs as 10-digit zero-padded decimal values.</summary>
    public static string FormatSpi(uint spi) => spi.ToString("D10", CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses <c>Security-Server</c>, picks the highest-q mechanism we actually offered, and
    /// returns the value to echo in <c>Security-Verify</c>.
    /// </summary>
    public static SecurityAgreement? ParseSecurityServer(string headerValue, SecurityProposal offered)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
            return null;

        var items = SplitHeaderValues(headerValue);
        if (items.Count == 0)
            return null;

        var candidates = new List<(double Q, SecurityAgreement Agreement)>();

        foreach (var item in items)
        {
            var parameters = ParseMechanism(item, out var name, out var q);
            if (parameters is null)
                continue;
            if (!name.Equals(MechanismName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!TryGet(parameters, "prot", out var prot) ||
                !prot.Equals("esp", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!TryGet(parameters, "mod", out var mod) ||
                !mod.Equals("trans", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!TryGet(parameters, "alg", out var alg) ||
                !Contains(DefaultIntegrityAlgorithms, alg))
                continue;
            if (!TryGet(parameters, "ealg", out var ealg) ||
                !Contains(DefaultEncryptionAlgorithms, ealg))
                continue;

            if (!TryGetUint(parameters, "spi-c", out var pcscfClientSpi) ||
                !TryGetUint(parameters, "spi-s", out var pcscfServerSpi) ||
                !TryGetInt(parameters, "port-c", out var pcscfClientPort) ||
                !TryGetInt(parameters, "port-s", out var pcscfServerPort))
                continue;

            // All four SPIs must be distinct, and the P-CSCF must not reuse ours.
            var spis = new[] { pcscfClientSpi, pcscfServerSpi, offered.SpiClient, offered.SpiServer };
            if (spis.Distinct().Count() != spis.Length)
                continue;

            if (!IsValidProtectedPort(pcscfClientPort) || !IsValidProtectedPort(pcscfServerPort) ||
                pcscfClientPort == pcscfServerPort)
                continue;

            var selected = offered with
            {
                IntegrityAlgorithm = alg,
                EncryptionAlgorithm = ealg
            };

            candidates.Add((q, new SecurityAgreement(
                Selected: selected,
                // The reference implementation echoes the whole header value verbatim,
                // including mechanisms we did not accept — do not rebuild it from the winner.
                VerifyValue: string.Join(", ", items),
                PcscfClientSpi: pcscfClientSpi,
                PcscfServerSpi: pcscfServerSpi,
                PcscfClientPort: pcscfClientPort,
                PcscfServerPort: pcscfServerPort)));
        }

        return candidates.Count == 0
            ? null
            : candidates.OrderByDescending(c => c.Q).First().Agreement;
    }

    /// <summary>
    /// Derives the IPsec keys from the AKA CK/IK.
    /// </summary>
    /// <remarks>
    /// hmac-sha-1-96 needs a 20-byte key, so IK is right-padded with four zero bytes.
    /// des-ede3-cbc needs 24 bytes: CK followed by its first 8 bytes with odd parity forced.
    /// </remarks>
    public static (byte[] IntegrityKey, byte[] EncryptionKey) ExpandKeys(
        byte[] ck, byte[] ik, string integrityAlgorithm, string encryptionAlgorithm)
    {
        ArgumentNullException.ThrowIfNull(ck);
        ArgumentNullException.ThrowIfNull(ik);
        if (ck.Length != 16 || ik.Length != 16)
            throw new ArgumentException("CK and IK must each be 16 bytes.");

        byte[] integrityKey = integrityAlgorithm switch
        {
            "hmac-md5-96" => (byte[])ik.Clone(),
            "hmac-sha-1-96" or "" => Concat(ik, new byte[4]),
            _ => throw new ArgumentException($"Unsupported integrity algorithm '{integrityAlgorithm}'.")
        };

        byte[] encryptionKey = encryptionAlgorithm switch
        {
            "null" => Array.Empty<byte>(),
            "aes-cbc" or "" => (byte[])ck.Clone(),
            "des-ede3-cbc" => Concat(ck, ApplyOddParity(ck[..8])),
            _ => throw new ArgumentException($"Unsupported encryption algorithm '{encryptionAlgorithm}'.")
        };

        return (integrityKey, encryptionKey);
    }

    /// <summary>
    /// Pair identifier used when installing the SA. Mirrors the reference implementation:
    /// XOR of the two SPIs, with a fixed tweak on collision.
    /// </summary>
    public static uint PairRequestId(uint ueSpi, uint pcscfSpi)
    {
        var reqid = (ueSpi ^ pcscfSpi) & 0x7fff_ffff;
        return reqid == 0 ? 0x4000_0000 : reqid;
    }

    /// <summary>Protected ports may be anything above 1024 except the SIP defaults.</summary>
    public static bool IsValidProtectedPort(int port) =>
        port > 1024 && port <= 65535 && port != 5060 && port != 5061;

    // ── Parsing helpers ──────────────────────────────────────────────────────

    /// <summary>Splits a comma-separated header while respecting quotes and angle brackets.</summary>
    public static List<string> SplitHeaderValues(string value)
    {
        var parts = new List<string>();
        var depth = 0;
        var inQuotes = false;
        var builder = new StringBuilder();

        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    inQuotes = !inQuotes;
                    builder.Append(c);
                    break;
                case '<':
                    if (!inQuotes) depth++;
                    builder.Append(c);
                    break;
                case '>':
                    if (!inQuotes) depth--;
                    builder.Append(c);
                    break;
                case ',' when !inQuotes && depth == 0:
                    parts.Add(builder.ToString().Trim());
                    builder.Clear();
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        if (builder.Length > 0)
            parts.Add(builder.ToString().Trim());

        return parts;
    }

    /// <summary>
    /// Splits one mechanism into its parameters. Returns null when the value is not a
    /// parameterised mechanism.
    /// </summary>
    private static Dictionary<string, string>? ParseMechanism(string item, out string name, out double q)
    {
        name = string.Empty;
        q = 1.0;

        var separator = item.IndexOf(';');
        if (separator < 0)
            return null;

        name = item[..separator].Trim();

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in item[(separator + 1)..].Split(';'))
        {
            if (string.IsNullOrWhiteSpace(segment))
                continue;

            var eq = segment.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = segment[..eq].Trim();
            var value = segment[(eq + 1)..].Trim().Trim('"');
            parameters[key] = value;

            if (key.Equals("q", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                q = parsed;
            }
        }

        return parameters;
    }

    private static bool TryGet(Dictionary<string, string> parameters, string key, out string value) =>
        parameters.TryGetValue(key, out value!);

    private static bool TryGetUint(Dictionary<string, string> parameters, string key, out uint value)
    {
        value = 0;
        return parameters.TryGetValue(key, out var text) &&
               uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetInt(Dictionary<string, string> parameters, string key, out int value)
    {
        value = 0;
        return parameters.TryGetValue(key, out var text) &&
               int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool Contains(string[] set, string value) =>
        set.Any(x => x.Equals(value, StringComparison.OrdinalIgnoreCase));

    private static byte[] Concat(byte[] first, byte[] second)
    {
        var result = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, result, 0, first.Length);
        Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
        return result;
    }

    /// <summary>
    /// Forces odd parity on each byte, as 3DES keying requires: the low bit is the parity bit and
    /// must be set or cleared so the byte contains an odd number of one bits.
    /// </summary>
    private static byte[] ApplyOddParity(byte[] key)
    {
        var result = new byte[key.Length];
        for (var i = 0; i < key.Length; i++)
        {
            // Clear the parity bit first — simply OR-ing 0x01 is wrong whenever the low bit is
            // already set, which leaves the byte with an even popcount.
            var withoutParity = key[i] & 0xFE;
            var ones = System.Numerics.BitOperations.PopCount((uint)withoutParity);
            result[i] = (ones & 1) == 0 ? (byte)(withoutParity | 0x01) : (byte)withoutParity;
        }
        return result;
    }
}
