using VoWin.Models;

namespace VoWin.Services;

public interface ISmsApiService
{
    SmsApiSettings Settings { get; }
    GatewayStatusItem Indicator { get; }
    event Action? StatusChanged;
    Task SaveAndApplyAsync(SmsApiSettings settings, CancellationToken ct = default);
}
