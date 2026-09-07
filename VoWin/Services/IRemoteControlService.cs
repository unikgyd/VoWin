using VoWin.Models;

namespace VoWin.Services;

public interface IRemoteControlService
{
    RemoteControlSettings Settings { get; }
    string WeixinStatus { get; }
    string QqStatus { get; }
    string LastActivity { get; }
    string PairingCodeStatus { get; }
    event Action? StatusChanged;

    Task SaveAndRestartAsync(RemoteControlSettings settings, CancellationToken ct = default);
    Task<WeixinLoginStartResult> BeginWeixinLoginAsync(CancellationToken ct = default);
    Task<WeixinLoginPollResult> PollWeixinLoginAsync(string? verificationCode, CancellationToken ct = default);
    string CreatePairingCode();
    Task TestQqAsync(RemoteControlSettings settings, CancellationToken ct = default);
}
