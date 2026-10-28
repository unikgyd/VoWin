using System.Net;
using System.Text.RegularExpressions;
using VoSharp.Kernel.SipGateway;

namespace VoSharp.GatewayHost;

public sealed record GatewaySmsLine(bool AllowOutgoing = false, bool ForwardIncoming = false, string? Target = null);
public sealed record GatewaySmsPermission(bool AllowOutgoing = false, string? DefaultLine = null);
public sealed record GatewaySmsConfiguration(bool AllowOutgoing = false, bool ForwardIncoming = false,
    string? DefaultLine = null, Dictionary<string, GatewaySmsLine>? Lines = null,
    Dictionary<string, GatewaySmsPermission>? Permissions = null)
{
    public bool Active => AllowOutgoing || ForwardIncoming;
    public void Validate(IReadOnlyDictionary<string, string> accounts, bool messagesEnabled)
    {
        var lines = Lines ?? [];
        var permissions = Permissions ?? [];
        if (lines.Count > 64 || permissions.Count > 64 || Active && (!messagesEnabled || lines.Count == 0) ||
            lines.Any(pair => !Regex.IsMatch(pair.Key, "^modem-[0-9a-f]{64}$") || pair.Value == null ||
                ForwardIncoming && pair.Value.ForwardIncoming && pair.Value.Target is { Length: > 0 } target && !accounts.ContainsKey(target)) ||
            DefaultLine != null && (!lines.TryGetValue(DefaultLine, out var route) || AllowOutgoing && !route.AllowOutgoing) ||
            permissions.Any(pair => !accounts.ContainsKey(pair.Key) || pair.Value == null ||
                pair.Value.DefaultLine is { } line && (!lines.TryGetValue(line, out var selected) || AllowOutgoing && pair.Value.AllowOutgoing && !selected.AllowOutgoing)) ||
            AllowOutgoing && (!lines.Values.Any(line => line.AllowOutgoing) || !permissions.Values.Any(permission => permission.AllowOutgoing)) ||
            ForwardIncoming && !lines.Values.Any(line => line.ForwardIncoming))
            throw new ArgumentException("Invalid explicit SMS permissions or routes.");
    }
    public Dictionary<string, SipGatewayLineRoute> Routes() => (Lines ?? []).ToDictionary(pair => pair.Key,
        pair => new SipGatewayLineRoute { AllowIncomingCalls = false, AllowOutgoingCalls = false,
            AllowOutgoingSms = pair.Value.AllowOutgoing, ForwardIncomingSms = pair.Value.ForwardIncoming,
            SmsTarget = pair.Value.Target }, StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, SipExtensionPolicy> Policies(IEnumerable<string> accounts) => accounts.ToDictionary(
        account => account, account => new SipExtensionPolicy { AllowCalls = true,
            AllowSms = Permissions?.GetValueOrDefault(account)?.AllowOutgoing == true,
            DefaultLineId = Permissions?.GetValueOrDefault(account)?.DefaultLine }, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Ephemeral parent-owned loopback endpoint, passed only over private stdio.
/// Never persisted, displayed, accepted from the Web UI or used through a proxy.</summary>
public sealed record GatewaySmsCallback(int Port, string Token)
{
    public void Validate()
    {
        if (Port is < 1024 or > 65535 || Token == null || !Regex.IsMatch(Token, "^[0-9a-f]{64}$"))
            throw new ArgumentException("Invalid private SMS callback.");
    }
    public override string ToString() => "Private SMS callback (credential omitted)";
}
