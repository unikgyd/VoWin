using System.Net;
using System.Net.Sockets;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace VoSharp.Kernel.SipGateway;

/// <summary>Configuration for the private-network SIP B2BUA.</summary>
public sealed record SipGatewayOptions
{
    [JsonConverter(typeof(SipAddressJsonConverter))]
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;
    public int SipPort { get; init; } = 5060;
    public string Realm { get; init; } = "vowin.local";
    /// <summary>Presentation/reply identity only; never changes the authentication realm.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? IdentityDomain { get; init; }
    /// <summary>Null retains legacy ingress; an explicit empty list drops every datagram.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<string>? TrustedNetworks { get; init; }
    public string LocalRegistrarAlgorithm { get; init; } = "MD5";
    [JsonIgnore] public Dictionary<string, string> Accounts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public int MinimumRegistrationSeconds { get; init; } = 60;
    public int MaximumRegistrationSeconds { get; init; } = 3600;
    public int MaximumContactsPerAccount { get; init; } = 8;
    public bool TrustClientSdpAddress { get; init; }
    public bool AllowPublicBind { get; init; }
    /// <summary>The desktop is another incoming-call participant, not a fallback carrier route.</summary>
    public bool LocalIncomingEnabled { get; init; }
    public bool MessagesEnabled { get; init; }
    public bool ForwardIncomingSms { get; init; }
    public bool AllowOutgoingSms { get; init; }
    /// <summary>Required for a paid transport that does not implement its own durable admission.</summary>
    [JsonIgnore] public ISipSmsOperationStore? SmsOperationStore { get; init; }
    /// <summary>MT intents must be committed by the host with the actual inbox row; no historical scan.</summary>
    [JsonIgnore] public ISipSmsForwardStore? SmsForwardStore { get; init; }
    [JsonIgnore] public Dictionary<string, string> LocalAccountEpochs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonIgnore] public string? PeerCredentialEpoch { get; init; }
    public bool AllowCarrierCalls { get; init; } = true;
    public Dictionary<string, SipExtensionPolicy> ExtensionPolicies { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SipDialRule> DialRules { get; init; } = [];
    /// <summary>Null keeps the direct softphone registrar. A fixed peer needs no REGISTER.</summary>
    public SipGatewayPeer? Peer { get; init; }
    /// <summary>Desktop uses explicit module routes; empty legacy library options retain automatic routing.</summary>
    public bool RequireConfiguredLines { get; init; }
    public string? DefaultOutboundLineId { get; init; }
    public Dictionary<string, SipGatewayLineRoute> LineRoutes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    internal bool UsesConfiguredLines => RequireConfiguredLines || LineRoutes.Count > 0;

    /// <summary>Creates the independent, secret-bearing runtime snapshot; never use JSON for this copy.</summary>
    public SipGatewayOptions DeepClone() => this with
    {
        BindAddress = CloneAddress(BindAddress),
        TrustedNetworks = TrustedNetworks?.ToArray(),
        Accounts = new(Accounts, StringComparer.OrdinalIgnoreCase),
        LocalAccountEpochs = new(LocalAccountEpochs, StringComparer.OrdinalIgnoreCase),
        ExtensionPolicies = ExtensionPolicies.ToDictionary(pair => pair.Key, pair => pair.Value.DeepClone(), StringComparer.OrdinalIgnoreCase),
        DialRules = DialRules.Select(rule => rule with { }).ToList(),
        LineRoutes = LineRoutes.ToDictionary(pair => pair.Key, pair => pair.Value.DeepClone(), StringComparer.OrdinalIgnoreCase),
        Peer = Peer?.DeepClone()
    };

    internal static IPAddress CloneAddress(IPAddress address) => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
        ? new IPAddress(address.GetAddressBytes(), address.ScopeId) : new IPAddress(address.GetAddressBytes());

    internal string? ResolveOutboundLine(string? requested)
    {
        var line = string.IsNullOrWhiteSpace(requested) ? DefaultOutboundLineId : requested.Trim();
        if (line is not null && !IsValidLineId(line)) throw new SipGatewayLineRoutingException(400, "Invalid Carrier Line");
        if (!UsesConfiguredLines) return line;
        if (line is null) throw new SipGatewayLineRoutingException(400, "Explicit Carrier Line Required");
        var route = LineRoutes.FirstOrDefault(pair => pair.Key.Equals(line, StringComparison.OrdinalIgnoreCase)).Value;
        if (route is null || !route.Enabled || !route.AllowOutgoingCalls)
            throw new SipGatewayLineRoutingException(403, "Carrier Line Outbound Disabled");
        return line;
    }

    internal SipExtensionPolicy? PolicyFor(string extension) => ExtensionPolicies.FirstOrDefault(
        pair => pair.Key.Equals(extension, StringComparison.OrdinalIgnoreCase)).Value;

    internal bool IsPlausibleCarrierDestination(string number, bool sms = false)
    {
        // Retain the legacy ingress envelope, and extend it only for a real
        // configured numeric prefix plus a bounded canonical/national user.
        // This is framing, not authority: final line/ACL/E.164 checks follow.
        if (Regex.IsMatch(number, sms ? @"^\+?[0-9]{2,20}$" : @"^\+?[0-9*#]{2,20}$")) return true;
        if (number.Length > 24) return false;
        foreach (var route in LineRoutes.Values)
        {
            var prefix = route.Prefix;
            if (string.IsNullOrEmpty(prefix) || !Regex.IsMatch(prefix, @"\A[0-9]{1,8}\z") ||
                !number.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var remainder = number[prefix.Length..];
            if (Regex.IsMatch(remainder, @"\A\+[1-9][0-9]{6,14}\z") ||
                !string.IsNullOrEmpty(route.CountryCode) && remainder.Length == route.NationalNumberLength &&
                Regex.IsMatch(remainder, @"\A[0-9]{6,14}\z")) return true;
        }
        return false;
    }

    internal (string Number, string? Line) ResolveDestination(string number, string? requested, string extension, bool sms = false, bool fromPeer = false)
    {
        // A peer may deliberately use the same username as a local account.
        // It never inherits that account's paid grants or preferred line.
        var policy = fromPeer ? null : PolicyFor(extension);
        if (sms ? !AllowOutgoingSms || policy?.AllowSms == false : !AllowCarrierCalls || policy?.AllowCalls == false)
            throw new SipGatewayLineRoutingException(403, "Carrier Operation Disabled");
        if (fromPeer && (Peer == null || (sms ? !Peer.AllowOutboundMessages : !Peer.AllowOutboundCalls)))
            throw new SipGatewayLineRoutingException(403, "Peer Carrier Operation Disabled");
        var selected = string.IsNullOrWhiteSpace(requested) ? policy?.DefaultLineId : requested.Trim();
        if (string.IsNullOrWhiteSpace(selected))
        {
            var rule = DialRules.FirstOrDefault(rule => number.StartsWith(rule.Prefix, StringComparison.Ordinal) &&
                (rule.TotalLength == 0 || rule.TotalLength == number.Length));
            if (rule != null)
            {
                selected = rule.LineId;
                number = rule.Prepend + (rule.StripPrefix ? number[rule.Prefix.Length..] : number);
            }
        }
        if (string.IsNullOrWhiteSpace(selected))
        {
            // Only an unselected destination may consume a selection prefix.
            // Explicit/account/rule lines, especially a bound SMS reply's
            // original line, must not reinterpret national digits as a SIM.
            var prefixed = LineRoutes.Where(pair => !string.IsNullOrEmpty(pair.Value.Prefix) &&
                    number.StartsWith(pair.Value.Prefix, StringComparison.Ordinal))
                .OrderByDescending(pair => pair.Value.Prefix!.Length).FirstOrDefault();
            if (prefixed.Value != null)
            {
                selected = prefixed.Key;
                number = number[prefixed.Value.Prefix!.Length..];
            }
        }
        var line = ResolveOutboundLine(selected, sms);
        var allowedLines = fromPeer
            ? (sms ? Peer!.SmsSendLines : Peer!.OutgoingLines)
            : (sms ? policy?.SmsSendLines : policy?.OutgoingLines);
        // Check the FINAL selected line once. A denial never re-runs selection
        // against a different default/SIM, and an explicit list cannot admit an
        // unresolved legacy automatic line to the controller.
        if (allowedLines != null && (line == null || !allowedLines.Contains(line, StringComparer.OrdinalIgnoreCase)))
            throw new SipGatewayLineRoutingException(403, "Carrier Line Not Permitted For Principal");
        var route = line == null ? null : LineRoutes.FirstOrDefault(pair => pair.Key.Equals(line, StringComparison.OrdinalIgnoreCase)).Value;
        if (route?.NormalizeE164 == true)
        {
            if (!number.StartsWith("+", StringComparison.Ordinal) && !string.IsNullOrEmpty(route.CountryCode) &&
                number.Length == route.NationalNumberLength && Regex.IsMatch(number, @"\A[0-9]+\z"))
                number = "+" + route.CountryCode + number;
            if (!Regex.IsMatch(number, @"\A\+[1-9][0-9]{6,14}\z"))
                throw new SipGatewayLineRoutingException(484, "Invalid Normalized E164 Number");
        }
        else if (!Regex.IsMatch(number, @"^\+?[0-9*#]{2,20}$"))
            throw new SipGatewayLineRoutingException(484, "Invalid Normalized Number");
        return (number, line);
    }

    private string? ResolveOutboundLine(string? requested, bool sms)
    {
        if (!sms) return ResolveOutboundLine(requested);
        var line = string.IsNullOrWhiteSpace(requested) ? DefaultOutboundLineId : requested.Trim();
        if (line != null && !IsValidLineId(line)) throw new SipGatewayLineRoutingException(400, "Invalid Carrier Line");
        if (!UsesConfiguredLines) return line;
        if (line == null) throw new SipGatewayLineRoutingException(400, "Explicit Carrier Line Required");
        var route = LineRoutes.FirstOrDefault(pair => pair.Key.Equals(line, StringComparison.OrdinalIgnoreCase)).Value;
        if (route == null || !route.Enabled || !route.AllowOutgoingSms)
            throw new SipGatewayLineRoutingException(403, "Carrier Line SMS Disabled");
        return line;
    }

    internal SipGatewayLineRoute? IncomingRoute(string? slotId)
    {
        if (!UsesConfiguredLines) return new();
        var route = slotId is null ? null : LineRoutes.FirstOrDefault(pair => pair.Key.Equals(slotId, StringComparison.OrdinalIgnoreCase)).Value;
        return route is { Enabled: true, AllowIncomingCalls: true } ? route : null;
    }

    private static bool IsValidLineId(string value) => Regex.IsMatch(value, @"^[A-Za-z0-9_.:-]{1,128}$");

    private static void ValidatePolicyReferences(IReadOnlyList<string>? values, IEnumerable<string> members, bool lines)
    {
        if (values == null) return; // Null is the explicit legacy boundary.
        var known = new HashSet<string>(members, StringComparer.OrdinalIgnoreCase);
        if (values.Count > 64 || values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Count ||
            values.Any(value => value == null || (lines ? !IsValidLineId(value) :
                !Regex.IsMatch(value, @"^[A-Za-z0-9_.-]{1,64}$")) || !known.Contains(value)))
            throw new ArgumentException("Invalid, duplicate or unknown SIP policy reference.");
    }

    public void Validate() => ValidateCore(allowEmptyIdentity: false);

    /// <summary>Pure import validation may retain a fully disabled identity-free draft. A runtime still calls Validate.</summary>
    public void ValidateForImport() => ValidateCore(allowEmptyIdentity: true);

    private void ValidateCore(bool allowEmptyIdentity)
    {
        ArgumentNullException.ThrowIfNull(LineRoutes);
        ArgumentNullException.ThrowIfNull(Accounts);
        ArgumentNullException.ThrowIfNull(LocalAccountEpochs);
        ArgumentNullException.ThrowIfNull(ExtensionPolicies);
        ArgumentNullException.ThrowIfNull(DialRules);
        ArgumentNullException.ThrowIfNull(BindAddress);
        if (LocalRegistrarAlgorithm is not ("MD5" or "SHA-256"))
            throw new ArgumentException("SIP local registrar requires an explicit supported digest algorithm.");
        if (!string.IsNullOrEmpty(IdentityDomain) && !ValidIdentityDomain(IdentityDomain))
            throw new ArgumentException("SIP identity domain requires a safe hostname or private IP literal.");
        if (TrustedNetworks != null)
        {
            if (TrustedNetworks.Count > 32) throw new ArgumentException("Too many SIP trusted networks.");
            var networks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in TrustedNetworks)
            {
                if (!TryTrustedNetwork(value, BindAddress.AddressFamily, out var address, out var prefix) ||
                    !networks.Add(address!.ToString() + "/" + prefix.ToString(CultureInfo.InvariantCulture)))
                    throw new ArgumentException("SIP trusted networks must be distinct strict same-family private CIDRs.");
            }
        }
        if (LineRoutes.Count > 64 || LineRoutes.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != LineRoutes.Count ||
            LineRoutes.Any(pair => !IsValidLineId(pair.Key) || pair.Value is null ||
                pair.Value.IncomingTarget is { Length: > 0 } target && !Regex.IsMatch(target, @"^[A-Za-z0-9_+.*-]{1,64}$") ||
                pair.Value.SmsTarget is { Length: > 0 } smsTarget && !Regex.IsMatch(smsTarget, @"^[A-Za-z0-9_+.*-]{1,64}$") ||
                pair.Value.LocalIncomingTarget is { Length: > 0 } localTarget && !Regex.IsMatch(localTarget, @"^[A-Za-z0-9_+.*-]{1,64}$") ||
                pair.Value.LocalSmsTarget is { Length: > 0 } localSmsTarget && !Regex.IsMatch(localSmsTarget, @"^[A-Za-z0-9_+.*-]{1,64}$")))
            throw new ArgumentException("Invalid or duplicate SIP module route.");
        // A registrar can serve local extensions without an attached modem.
        // Carrier operations still require a valid explicit configured line.
        if (DialRules.Count > 64 || DialRules.Any(rule => rule == null ||
                !Regex.IsMatch(rule.Prefix, @"^\+?[0-9]{1,20}$") || rule.TotalLength is < 0 or > 24 ||
                rule.TotalLength > 0 && rule.TotalLength < rule.Prefix.Length ||
                !Regex.IsMatch(rule.Prepend, @"^\+?[0-9]{0,12}$") || !IsValidLineId(rule.LineId) ||
                !LineRoutes.ContainsKey(rule.LineId)))
            throw new ArgumentException("Invalid SIP number routing rule.");
        if (ExtensionPolicies.Any(pair => !Accounts.ContainsKey(pair.Key) || pair.Value == null ||
            pair.Value.DefaultLineId is { } line && (!IsValidLineId(line) || !LineRoutes.ContainsKey(line))))
            throw new ArgumentException("Invalid extension permissions or default line.");
        foreach (var policy in ExtensionPolicies.Values)
        {
            ValidatePolicyReferences(policy.OutgoingLines, LineRoutes.Keys, lines: true);
            ValidatePolicyReferences(policy.SmsSendLines, LineRoutes.Keys, lines: true);
        }
        var linePrefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in LineRoutes.Values)
        {
            if (!string.IsNullOrEmpty(route.Prefix) && (!Regex.IsMatch(route.Prefix, @"\A[0-9]{1,8}\z") || !linePrefixes.Add(route.Prefix)) ||
                string.IsNullOrEmpty(route.CountryCode) && route.NationalNumberLength != 0 ||
                !string.IsNullOrEmpty(route.CountryCode) && (!Regex.IsMatch(route.CountryCode, @"\A[1-9][0-9]{0,2}\z") ||
                    route.NationalNumberLength < 6 || route.NationalNumberLength > 15 - route.CountryCode.Length))
                throw new ArgumentException("Invalid or duplicate SIP line prefix or national number policy.");
            ValidatePolicyReferences(route.LocalIncomingExtensions, Accounts.Keys, lines: false);
            ValidatePolicyReferences(route.LocalSmsRecipients, Accounts.Keys, lines: false);
            if (route.ForwardCallsToPeer == true && (Peer == null || string.IsNullOrWhiteSpace(route.IncomingTarget)) ||
                route.ForwardSmsToPeer == true && (Peer == null || string.IsNullOrWhiteSpace(route.SmsTarget)))
                throw new ArgumentException("Explicit peer forwarding requires a peer and an explicit route target.");
        }
        if (DefaultOutboundLineId is { } defaultLine)
        {
            if (!IsValidLineId(defaultLine)) throw new ArgumentException("Invalid default SIP module line.");
            if (UsesConfiguredLines && !LineRoutes.Any(pair => pair.Key.Equals(defaultLine, StringComparison.OrdinalIgnoreCase) &&
                pair.Value.Enabled))
                throw new ArgumentException("默认外拨线路未接入 VoIP 或已关闭外拨；请选择有效线路或改为每次指定。");
        }
        if (BindAddress.Equals(IPAddress.Any) || BindAddress.Equals(IPAddress.IPv6Any))
            throw new ArgumentException("SIP gateway requires an explicit WireGuard/private address; wildcard binding is disabled.");
        if (!AllowPublicBind && !IsPrivateOrLoopback(BindAddress))
            throw new ArgumentException($"{BindAddress} is not a private/WireGuard address. Set AllowPublicBind only when an external firewall is enforced.");
        if (SipPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(SipPort));
        if (!Regex.IsMatch(Realm, @"^[A-Za-z0-9_.-]{1,253}$")) throw new ArgumentException("Use a simple SIP realm/domain.", nameof(Realm));
        if (!allowEmptyIdentity && Accounts.Count == 0 && Peer == null) throw new ArgumentException("At least one SIP extension or an explicitly authenticated peer is required.", nameof(Accounts));
        if (Accounts.Count > 64 || Accounts.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Accounts.Count ||
            Accounts.Any(pair => !Regex.IsMatch(pair.Key, @"^[A-Za-z0-9_.-]{1,64}$") || string.IsNullOrEmpty(pair.Value)))
            throw new ArgumentException("SIP extension names and passwords cannot be empty.", nameof(Accounts));
        if (LocalAccountEpochs.Count > 64 || LocalAccountEpochs.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != LocalAccountEpochs.Count ||
            LocalAccountEpochs.Any(pair => !Accounts.ContainsKey(pair.Key) || !IsCredentialEpoch(pair.Value)) ||
            PeerCredentialEpoch != null && !IsCredentialEpoch(PeerCredentialEpoch))
            throw new ArgumentException("Invalid SIP message recipient credential epoch.");
        if (MinimumRegistrationSeconds < 1 || MaximumRegistrationSeconds < MinimumRegistrationSeconds)
            throw new ArgumentException("Invalid registration expiry range.");
        if (MaximumContactsPerAccount is < 1 or > 32) throw new ArgumentException("Invalid contact limit.");
        if (Peer is { } peer)
        {
            if (peer.EndPoint == null || !IsPrivateOrLoopback(peer.EndPoint.Address) ||
                peer.EndPoint.Address.Equals(IPAddress.Any) || peer.EndPoint.Address.Equals(IPAddress.IPv6Any) ||
                peer.EndPoint.Address.AddressFamily != BindAddress.AddressFamily || peer.EndPoint.Port is < 1 or > 65535)
                throw new ArgumentException("SIP peer requires a private IP and port in the same address family as the local binding.");
            if (peer.EndPoint.Equals(new IPEndPoint(BindAddress, SipPort)))
                throw new ArgumentException("SIP peer cannot be the gateway itself.");
            if (string.IsNullOrEmpty(peer.IncomingTarget) || !Regex.IsMatch(peer.IncomingTarget, @"^[A-Za-z0-9_+.*-]{1,64}$"))
                throw new ArgumentException("SIP peer requires a valid incoming target.");
            if (peer.InboundAuth == null || peer.OutboundAuth == null || !string.IsNullOrEmpty(peer.Password))
                throw new ArgumentException("Legacy SIP peer credentials require explicit typed inbound/outbound authentication and a pinned outbound realm; construct a new typed peer.");
            peer.InboundAuth.Validate();
            peer.OutboundAuth.Validate();
            ValidatePolicyReferences(peer.OutgoingLines, LineRoutes.Keys, lines: true);
            ValidatePolicyReferences(peer.SmsSendLines, LineRoutes.Keys, lines: true);
        }
    }

    internal string IdentityHost => string.IsNullOrEmpty(IdentityDomain) ? Realm :
        IPAddress.TryParse(IdentityDomain, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6
            ? "[" + address + "]" : IdentityDomain!;

    internal bool IsTrustedSource(IPAddress source)
    {
        if (TrustedNetworks == null) return true;
        foreach (var value in TrustedNetworks)
            if (TryTrustedNetwork(value, source.AddressFamily, out var network, out var prefix) && NetworkContains(network!, prefix, source))
                return true;
        return false;
    }

    private static bool ValidIdentityDomain(string value)
    {
        if (value.Length > 253 || value.Contains('%') || value.Any(char.IsWhiteSpace)) return false;
        if (IPAddress.TryParse(value, out var address))
            return MinimumPrivatePrefix(address) >= 0 &&
                (address.AddressFamily != AddressFamily.InterNetwork || value == address.ToString());
        if (value.All(character => character is >= '0' and <= '9' or '.')) return false;
        return Regex.IsMatch(value, @"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*\z");
    }

    private static int MinimumPrivatePrefix(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return -1;
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            if (bytes[0] is 10 or 127) return 8;
            if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) return 12;
            return bytes[0] == 192 && bytes[1] == 168 ? 16 : -1;
        }
        if (address.Equals(IPAddress.IPv6Loopback)) return 128;
        return bytes.Length == 16 && (bytes[0] & 0xfe) == 0xfc ? 7 : -1;
    }

    private static bool TryTrustedNetwork(string? value, AddressFamily family, out IPAddress? address, out int prefix)
    {
        address = null; prefix = 0;
        if (value == null || value.Length is < 3 or > 49 || value.Contains('%') || value.Any(char.IsWhiteSpace)) return false;
        var slash = value.IndexOf('/');
        if (slash <= 0 || slash != value.LastIndexOf('/') ||
            !IPAddress.TryParse(value.AsSpan(0, slash), out address) || address.AddressFamily != family ||
            !int.TryParse(value.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out prefix)) return false;
        var minimum = MinimumPrivatePrefix(address);
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4 && value[..slash] != address.ToString()) return false;
        if (minimum < 0 || prefix < minimum || prefix > bytes.Length * 8) return false;
        var complete = prefix / 8;
        var remaining = prefix % 8;
        if (remaining != 0 && (bytes[complete] & ((1 << (8 - remaining)) - 1)) != 0) return false;
        for (var index = complete + (remaining == 0 ? 0 : 1); index < bytes.Length; index++)
            if (bytes[index] != 0) return false;
        return true;
    }

    private static bool NetworkContains(IPAddress network, int prefix, IPAddress source)
    {
        var networkBytes = network.GetAddressBytes();
        var sourceBytes = source.GetAddressBytes();
        if (networkBytes.Length != sourceBytes.Length) return false;
        var complete = prefix / 8;
        var remaining = prefix % 8;
        if (!networkBytes.AsSpan(0, complete).SequenceEqual(sourceBytes.AsSpan(0, complete))) return false;
        return remaining == 0 || (networkBytes[complete] & (0xff << (8 - remaining))) == (sourceBytes[complete] & (0xff << (8 - remaining)));
    }

    internal static bool IsPrivateOrLoopback(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return bytes[0] == 10 ||
                   bytes[0] == 127 ||
                   bytes[0] == 192 && bytes[1] == 168 ||
                   bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                   bytes[0] == 169 && bytes[1] == 254;
        }
        return address.IsIPv6LinkLocal || (bytes.Length == 16 && (bytes[0] & 0xfe) == 0xfc);
    }

    private static bool IsCredentialEpoch(string? value) => value is { Length: 32 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>Module exposure is independent of its IMS registration and the gateway listener.</summary>
public sealed record SipGatewayLineRoute
{
    public bool Enabled { get; init; } = true;
    public bool AllowIncomingCalls { get; init; } = true;
    public bool AllowOutgoingCalls { get; init; } = true;
    /// <summary>Empty inherits the trunk target; direct registrar mode filters to this extension.</summary>
    public string? IncomingTarget { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool ForwardIncomingSms { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool AllowOutgoingSms { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SmsTarget { get; init; }
    /// <summary>Independent local registrar filter; empty rings every permitted local registration.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? LocalIncomingTarget { get; init; }
    /// <summary>Independent local registrar SMS recipient; empty targets permitted local registrations.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? LocalSmsTarget { get; init; }
    /// <summary>Null keeps the legacy scalar/all policy; an empty list means nobody.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<string>? LocalIncomingExtensions { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<string>? LocalSmsRecipients { get; init; }
    /// <summary>Null keeps legacy forwarding. Explicit true requires this route's target.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? ForwardCallsToPeer { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? ForwardSmsToPeer { get; init; }
    /// <summary>Optional explicit SIM-selection prefix; longest match wins only when no line was selected.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Prefix { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? CountryCode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int NationalNumberLength { get; init; }
    /// <summary>Opt-in strict E.164 after this line's explicit national conversion; false preserves legacy dialing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool NormalizeE164 { get; init; }
    public SipGatewayLineRoute DeepClone() => this with
    { LocalIncomingExtensions = LocalIncomingExtensions?.ToArray(), LocalSmsRecipients = LocalSmsRecipients?.ToArray() };
}

public sealed record SipExtensionPolicy
{
    public bool AllowCalls { get; init; } = true;
    public bool AllowSms { get; init; }
    public string? DefaultLineId { get; init; }
    /// <summary>Separate from legacy paid AllowCalls/AllowSms; null retains legacy access.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? AllowInternalCalls { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? AllowMessages { get; init; }
    /// <summary>Null is legacy unrestricted selection; [] denies all paid lines.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<string>? OutgoingLines { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<string>? SmsSendLines { get; init; }
    public SipExtensionPolicy DeepClone() => this with
    { OutgoingLines = OutgoingLines?.ToArray(), SmsSendLines = SmsSendLines?.ToArray() };
}

public sealed record SipDialRule
{
    public string Prefix { get; init; } = "";
    public int TotalLength { get; init; }
    public bool StripPrefix { get; init; }
    public string Prepend { get; init; } = "";
    public string LineId { get; init; } = "";
}

internal sealed class SipGatewayLineRoutingException(int statusCode, string reason) : Exception(reason)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>Private UDP trunk. Credentials are used only with the pinned signaling endpoint.</summary>
public sealed record SipGatewayPeer([property: JsonConverter(typeof(SipEndPointJsonConverter))] IPEndPoint EndPoint,
    string IncomingTarget, string Username, [property: JsonIgnore] string Password)
{
    /// <summary>Explicit migration boundary: the four-string constructor alone is not runnable.</summary>
    public SipGatewayPeer(IPEndPoint endPoint, string incomingTarget, SipPeerInboundCredentials inboundAuth, SipPeerOutboundCredentials outboundAuth)
        : this(endPoint, incomingTarget, (inboundAuth ?? throw new ArgumentNullException(nameof(inboundAuth))).Username, string.Empty)
    {
        InboundAuth = inboundAuth;
        OutboundAuth = outboundAuth ?? throw new ArgumentNullException(nameof(outboundAuth));
    }

    public SipPeerInboundCredentials? InboundAuth { get; init; }
    public SipPeerOutboundCredentials? OutboundAuth { get; init; }
    // Deliberately opt-in: an incoming-call bridge must not implicitly enable toll calls.
    public bool AllowOutboundCalls { get; init; }
    public bool AllowOutboundMessages { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<string>? OutgoingLines { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<string>? SmsSendLines { get; init; }
    /// <summary>Carrier -> peer directions, independent of paid outbound grants.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? AllowIncomingCalls { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? AllowIncomingMessages { get; init; }
    public SipGatewayPeer DeepClone() => this with
    {
        EndPoint = new IPEndPoint(SipGatewayOptions.CloneAddress(EndPoint.Address), EndPoint.Port),
        InboundAuth = InboundAuth is { } inbound ? inbound with { } : null,
        OutboundAuth = OutboundAuth?.DeepClone(),
        OutgoingLines = OutgoingLines?.ToArray(), SmsSendLines = SmsSendLines?.ToArray()
    };
    public override string ToString() => $"SIP peer {EndPoint}"; // Never interpolate credentials into diagnostics.
}

// Public diagnostic JSON contains string network values, never IPAddress's
// runtime-only properties (IPv4 ScopeId throws). This is not a secret-bearing
// runtime snapshot; credentials remain JsonIgnore and cloning stays explicit.
internal sealed class SipAddressJsonConverter : JsonConverter<IPAddress>
{
    public SipAddressJsonConverter() { }
    public override IPAddress Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new JsonException("SIP diagnostic addresses are write-only; import a deployment profile instead.");
    public override void Write(Utf8JsonWriter writer, IPAddress value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

internal sealed class SipEndPointJsonConverter : JsonConverter<IPEndPoint>
{
    public SipEndPointJsonConverter() { }
    public override IPEndPoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new JsonException("SIP diagnostic endpoints are write-only; import a deployment profile instead.");
    public override void Write(Utf8JsonWriter writer, IPEndPoint value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

public sealed record SipGatewayRegistration(
    string Extension,
    string Contact,
    IPEndPoint RemoteEndPoint,
    DateTimeOffset ExpiresAt,
    string UserAgent);

public sealed record SipIncomingCallClaimed(string CallId, string? SlotId, bool IsLocal);

public sealed record SipGatewayStatus(
    bool IsRunning,
    IPEndPoint? LocalEndPoint,
    IReadOnlyList<SipGatewayRegistration> Registrations,
    int ActiveDialogs)
{
    public IPEndPoint? PeerEndPoint { get; init; }
    public string? IncomingTarget { get; init; }
    public bool RequiresConfiguredLines { get; init; }
    public bool PeerAllowsOutboundCalls { get; init; }
    public bool LocalIncomingEnabled { get; init; }
    public bool MessagesEnabled { get; init; }
    public bool OutgoingSmsEnabled { get; init; }
    public long MessagesForwarded { get; init; }
    public long MessageFailures { get; init; }
    public IReadOnlyDictionary<string, SipGatewayLineRoute> LineRoutes { get; init; } = new Dictionary<string, SipGatewayLineRoute>();
}
