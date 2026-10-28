using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoSharp.Kernel.SipGateway;

namespace VoWin.Services;

/// <summary>User-selected, secret-bearing deployment export; never starts SIP or changes carrier routing.</summary>
internal sealed partial record SipGatewayDeploymentProfile(SipGatewayPreferences Settings, string Password,
    Dictionary<string, string>? ExtensionPasswords = null, [property: JsonIgnore] string? PeerInboundPassword = null,
    [property: JsonIgnore] string? PeerOutboundPassword = null)
{
    public override string ToString() => "SIP deployment profile (secret omitted)";
    [JsonIgnore] internal SipGatewayCredentials Credentials => Settings.UseTrunk && Settings.CredentialVersion < 2
        ? new(Extensions: ExtensionPasswords, LegacyTrunkPassword: Password ?? "")
        : new(Settings.HasLocalRegistrar ? Password ?? "" : "", ExtensionPasswords,
            PeerInboundPassword ?? "", PeerOutboundPassword ?? "");

    internal static SipGatewayOptions BuildOptions(SipGatewayPreferences settings, string password, Dictionary<string, string>? extensionPasswords = null)
        => BuildOptions(settings, settings.UseTrunk
            ? new(Extensions: extensionPasswords, LegacyTrunkPassword: password)
            : new(password, extensionPasswords));

    internal static SipGatewayOptions BuildOptions(SipGatewayPreferences settings, SipGatewayCredentials credentials)
        => BuildOptions(settings, credentials, allowEmptyDraft: false);

    internal static SipGatewayOptions BuildOptions(SipGatewayPreferences settings, SipGatewayCredentials credentials, bool allowEmptyDraft)
    {
        if (!IPAddress.TryParse(settings.BindAddress, out var local))
            throw new ArgumentException("请输入本机已分配的局域网 / VPN IP。");
        if (settings.CredentialVersion is not (0 or 2)) throw new ArgumentException("不支持的 SIP 凭据版本。");
        if (settings.LocalAccountEpochs is { } epochs && (epochs.Count > 64 ||
                epochs.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != epochs.Count ||
                epochs.Values.Any(epoch => !SipSmsRecipientAuthority.IsValidEpoch(epoch))) ||
            settings.PeerCredentialEpoch != null && !SipSmsRecipientAuthority.IsValidEpoch(settings.PeerCredentialEpoch) ||
            settings.CredentialVersion == 0 && (settings.LocalAccountEpochs != null || settings.PeerCredentialEpoch != null))
            throw new ArgumentException("SIP 接收身份无效或重复；请重新保存配置，未猜测旧短信归属。");
        SipGatewayPeer? peer = null;
        if (settings.UseTrunk)
        {
            if (settings.CredentialVersion != 2)
                throw new ArgumentException("旧中继配置需补齐独立入站 / 出站凭据及预期 Realm，再保存启用。");
            if (!IPAddress.TryParse(settings.ServerAddress, out var server))
                throw new ArgumentException("请输入 SIP 服务器的私网 / VPN IP。");
            if (string.IsNullOrWhiteSpace(settings.PeerExpectedRealm) || settings.PeerExpectedRealm == "*")
                throw new ArgumentException("请填写服务器实际使用的预期 Realm；不接受自动学习或通配符。");
            peer = new(new IPEndPoint(server, settings.ServerPort), settings.IncomingTarget,
                new SipPeerInboundCredentials(settings.TrunkUsername, credentials.PeerInboundPassword)
                { Algorithm = settings.PeerInboundAlgorithm ?? "SHA-256" },
                new SipPeerOutboundCredentials(settings.PeerOutboundUsername ?? "", credentials.PeerOutboundPassword, settings.PeerExpectedRealm)
                { AllowedAlgorithms = (settings.PeerAllowedAlgorithms ?? ["SHA-256"]).ToArray() })
            {
                AllowOutboundCalls = settings.PeerAllowOutboundCalls ?? settings.AllowOutboundCalls,
                AllowOutboundMessages = settings.PeerAllowOutboundMessages ?? settings.AllowOutgoingSms,
                AllowIncomingCalls = settings.PeerAllowIncomingCalls, AllowIncomingMessages = settings.PeerAllowIncomingMessages,
                OutgoingLines = settings.PeerOutgoingLines?.ToArray(), SmsSendLines = settings.PeerSmsSendLines?.ToArray()
            };
        }
        var accounts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var policies = new Dictionary<string, SipExtensionPolicy>(StringComparer.OrdinalIgnoreCase);
        if (settings.HasLocalRegistrar)
        {
            if (string.IsNullOrWhiteSpace(settings.Extension) || string.IsNullOrEmpty(credentials.MainPassword))
                throw new ArgumentException("本地分机账号和密码不能为空；只使用中继时可关闭本地分机服务。");
            accounts.Add(settings.Extension, credentials.MainPassword);
            if (settings.PrimaryPolicy != null) policies.Add(settings.Extension, settings.PrimaryPolicy);
            foreach (var extension in settings.Extensions ?? [])
            {
                if (!accounts.TryAdd(extension.Username, credentials.Extensions?.GetValueOrDefault(extension.Username) ?? ""))
                    throw new ArgumentException("分机号重复，请为每台分机设置独立账号。");
                policies.Add(extension.Username, extension.Policy);
            }
        }
        var options = new SipGatewayOptions
        {
            BindAddress = local, SipPort = settings.Port, Realm = settings.Realm, Peer = peer,
            IdentityDomain = settings.IdentityDomain, TrustedNetworks = settings.TrustedNetworks?.ToArray(),
            LocalRegistrarAlgorithm = settings.LocalRegistrarAlgorithm ?? "MD5",
            Accounts = accounts, ExtensionPolicies = policies,
            LocalAccountEpochs = accounts.Keys.ToDictionary(username => username,
                username => settings.LocalAccountEpochs?.FirstOrDefault(pair => pair.Key.Equals(username, StringComparison.OrdinalIgnoreCase)).Value
                    ?? Guid.NewGuid().ToString("N"), StringComparer.OrdinalIgnoreCase),
            PeerCredentialEpoch = peer == null ? null : settings.PeerCredentialEpoch ?? Guid.NewGuid().ToString("N"),
            AllowCarrierCalls = settings.AllowOutboundCalls,
            MessagesEnabled = settings.MessagesEnabled, ForwardIncomingSms = settings.ForwardIncomingSms,
            AllowOutgoingSms = settings.AllowOutgoingSms, DialRules = settings.DialRules ?? [],
            RequireConfiguredLines = true, DefaultOutboundLineId = settings.DefaultOutboundLineId,
            LineRoutes = settings.LineRoutes
        };
        if (allowEmptyDraft && !settings.UseTrunk && !settings.HasLocalRegistrar) options.ValidateForImport();
        else options.Validate();
        return options.DeepClone();
    }
    internal static Task<SipGatewayDeploymentProfile> ReadAsync(string path) => ReadProfileAsync(path);
}
