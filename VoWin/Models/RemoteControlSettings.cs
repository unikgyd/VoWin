namespace VoWin.Models;

public sealed class RemoteControlSettings
{
    public bool WeixinEnabled { get; set; }
    public string WeixinBaseUrl { get; set; } = "https://ilinkai.weixin.qq.com";
    public string WeixinToken { get; set; } = string.Empty;
    public string WeixinBotId { get; set; } = string.Empty;
    public string WeixinUserId { get; set; } = string.Empty;
    public string WeixinAllowedUsers { get; set; } = string.Empty;

    public bool QqEnabled { get; set; }
    public string QqAppId { get; set; } = string.Empty;
    public string QqClientSecret { get; set; } = string.Empty;
    public string QqAllowedUsers { get; set; } = string.Empty;

    public bool NotifyIncomingSms { get; set; } = true;
    public bool NotifyOtpOnly { get; set; }
    public bool NotifyIncomingCalls { get; set; } = true;

    public RemoteControlSettings Clone() => (RemoteControlSettings)MemberwiseClone();
}

public enum RemoteChannelKind
{
    Weixin,
    Qq
}

public sealed record RemoteInboundMessage(
    RemoteChannelKind Channel,
    string SenderId,
    string ConversationId,
    string Text,
    string MessageId,
    string? ContextToken = null,
    bool IsGroup = false);

public sealed record RemoteReplyTarget(
    RemoteChannelKind Channel,
    string ConversationId,
    string? MessageId = null,
    string? ContextToken = null,
    bool IsGroup = false);

public sealed record WeixinLoginStartResult(bool Success, string? QrContent, string Message);

public sealed record WeixinLoginPollResult(
    bool Connected,
    bool NeedsVerificationCode,
    string Message,
    string? Token = null,
    string? BotId = null,
    string? BaseUrl = null,
    string? UserId = null);
