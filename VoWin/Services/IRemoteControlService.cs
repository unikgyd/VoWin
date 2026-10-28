using VoWin.Models;

namespace VoWin.Services;

public interface IRemoteControlService
{
    RemoteControlSettings Settings { get; }
    string WeixinStatus { get; }
    string QqStatus { get; }
    string TelegramStatus { get; }
    string BarkStatus { get; }
    GatewayStatusItem TelegramIndicator { get; }
    GatewayStatusItem BarkIndicator { get; }
    GatewayStatusItem WeixinIndicator { get; }
    GatewayStatusItem QqIndicator { get; }
    string LastActivity { get; }
    string PairingCodeStatus { get; }
    event Action? StatusChanged;

    Task SaveAndRestartAsync(RemoteControlSettings settings, CancellationToken ct = default);
    Task<WeixinLoginStartResult> BeginWeixinLoginAsync(CancellationToken ct = default);
    Task<WeixinLoginPollResult> PollWeixinLoginAsync(string? verificationCode, CancellationToken ct = default);
    string CreatePairingCode();
    Task TestQqAsync(RemoteControlSettings settings, CancellationToken ct = default);
    Task TestTelegramAsync(RemoteControlSettings settings, CancellationToken ct = default);
    Task TestBarkAsync(RemoteControlSettings settings, CancellationToken ct = default);
}
