namespace VoWin.Services;

public partial class VoKernelService
{
    internal async Task RestoreSipGatewayOnStartupAsync(SipGatewaySettingsStore? store = null)
    {
        try
        {
            store ??= new();
            var settings = await store.LoadAsync().ConfigureAwait(false);
            if (!settings.RestoreOnStartup) return;
            settings = await store.MigrateLegacyDeploymentAsync(settings).ConfigureAwait(false);
            if (!settings.WasEnabled) return;
            var credentials = await store.LoadCredentialsAsync(settings).ConfigureAwait(false);
            settings = await store.PrepareConnectionAsync(settings, credentials, enabled: true).ConfigureAwait(false);
            var options = SipGatewayDeploymentProfile.BuildOptions(settings, credentials);
            await StartSipGatewayAsync(options).ConfigureAwait(false);
            AddLog("INFO", "SipStartup", "已自动加载记住的 SIP 配置，并恢复上次已启用状态；模组注册独立恢复。");
        }
        catch (Exception ex)
        {
            AddLog("WARN", "SipStartup", $"SIP 启动恢复失败（{ex.GetType().Name}），未绕过配置校验；请检查监听地址、端口和记住的凭据。");
        }
    }
}
