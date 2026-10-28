using System.IO;
using System.Reflection;
using System.Text.Json;
using VoSharp.Kernel;
using VoSharp.Kernel.SipGateway;
using VoWin.Services;
using VoWin.ViewModels.Pages;

namespace VoWin.Tests;

public sealed class SipDeploymentLineProjectionSettingsTests
{
    private static SipGatewayLineRoute Route() => new()
    {
        Enabled = true, AllowOutgoingCalls = false, AllowOutgoingSms = false,
        Prefix = "81", CountryCode = "86", NationalNumberLength = 11, NormalizeE164 = true
    };

    [Fact]
    public void DeploymentProjectionLineEditorCopiesEveryNormalizationFieldWithoutChangingLegacyDefaults()
    {
        var source = Route(); var item = new SipModuleRouteItem("synthetic-line", source);
        Assert.Equal("81", item.Prefix); Assert.Equal("86", item.CountryCode);
        Assert.Equal(11, item.NationalNumberLength); Assert.True(item.NormalizeE164);
        Assert.Equal(JsonSerializer.Serialize(source), JsonSerializer.Serialize(item.Snapshot()));
        var legacy = new SipModuleRouteItem("synthetic-legacy").Snapshot();
        Assert.Null(legacy.Prefix); Assert.Null(legacy.CountryCode);
        Assert.Equal(0, legacy.NationalNumberLength); Assert.False(legacy.NormalizeE164);
    }

    [Fact]
    public async Task DeploymentProjectionLinePreferencesPreserveADormantDefaultAndItsNormalizationMetadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vowin-line-projection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SipGatewaySettingsStore(Path.Combine(directory, "sip.json"));
            var settings = new SipGatewayPreferences
            {
                BindAddress = "127.0.0.1", Extension = "main", DefaultOutboundLineId = "synthetic-line",
                LineRoutes = new() { ["synthetic-line"] = Route() }
            };
            await store.SaveAsync(settings);
            var loaded = await store.LoadAsync();
            Assert.Equal("synthetic-line", loaded.DefaultOutboundLineId);
            Assert.Equal(JsonSerializer.Serialize(settings.LineRoutes), JsonSerializer.Serialize(loaded.LineRoutes));
            var options = SipGatewayDeploymentProfile.BuildOptions(loaded, "synthetic-primary-password");
            Assert.Equal("synthetic-line", options.DefaultOutboundLineId);
            Assert.Equal("81", options.LineRoutes["synthetic-line"].Prefix);
            Assert.False(options.LineRoutes["synthetic-line"].AllowOutgoingCalls);
            Assert.False(options.LineRoutes["synthetic-line"].AllowOutgoingSms);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task DeploymentProjectionLineUiLoadRefreshAndSaveRetainEditedFieldsWithoutStartingAService()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vowin-line-ui-projection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "sip.json"); var store = new SipGatewaySettingsStore(path);
            var settings = new SipGatewayPreferences
            {
                CredentialVersion = 2, LocalRegistrarEnabled = true, BindAddress = "127.0.0.1", Realm = "gateway.invalid",
                Extension = "main", RestoreOnStartup = true, DefaultOutboundLineId = "synthetic-line",
                LineRoutes = new() { ["synthetic-line"] = Route() },
                PrimaryPolicy = new() { AllowCalls = false, AllowSms = false, OutgoingLines = [], SmsSendLines = [] }
            };
            await store.SaveConnectionAsync(settings, new SipGatewayCredentials("synthetic-primary-password"), enabled: false);
            await using var kernel = new VoKernel();
            var service = DispatchProxy.Create<IVoKernelService, PhoneCallServiceProxy>();
            ((PhoneCallServiceProxy)service).Handler = (method, _) => method.Name switch
            {
                "get_Kernel" => kernel, "GetSipGatewayStatus" => null,
                _ when method.Name.StartsWith("add_", StringComparison.Ordinal) => null,
                _ => throw new NotSupportedException("No runtime operation authorized by this synthetic test")
            };
            var model = new SettingsViewModel(service, store); await model.LoadSipSettingsAsync();
            var row = Assert.Single(model.SipModuleRoutes);
            Assert.Equal("81", row.Prefix); Assert.Equal("86", row.CountryCode);
            Assert.Equal(11, row.NationalNumberLength); Assert.True(row.NormalizeE164);
            row.Prefix = "82"; row.CountryCode = "63"; row.NationalNumberLength = 10;
            model.RefreshSipModuleRoutesCore();
            await model.SaveSipGatewaySettingsCommand.ExecuteAsync(null);
            var loaded = await new SipGatewaySettingsStore(path).LoadAsync();
            Assert.False(loaded.WasEnabled); Assert.Equal("synthetic-line", loaded.DefaultOutboundLineId);
            var restored = loaded.LineRoutes["synthetic-line"];
            Assert.Equal("82", restored.Prefix); Assert.Equal("63", restored.CountryCode);
            Assert.Equal(10, restored.NationalNumberLength); Assert.True(restored.NormalizeE164);
            Assert.Null(kernel.LocalSipGateway); Assert.Empty(kernel.Pool.Slots);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
