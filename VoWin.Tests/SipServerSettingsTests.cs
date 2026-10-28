using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VoSharp.Kernel.SipGateway;
using VoWin.Services;
using VoWin.ViewModels.Pages;

namespace VoWin.Tests;

public sealed class SipServerSettingsTests
{
    [Fact]
    public async Task AdditionalCredentialsAreProtectedAndServerRestoresWithoutAModem()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vowin-server-settings-" + Guid.NewGuid().ToString("N"));
        var store = new SipGatewaySettingsStore(Path.Combine(directory, "sip.json"));
        using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port; reservation.Dispose();
        await using var service = new VoKernelService(new PreferenceDatabaseService(Path.Combine(directory, "prefs.db")),
            new(Path.Combine(directory, "telephony.json")), Path.Combine(directory, "test.log"));
        var settings = new SipGatewayPreferences
        {
            BindAddress = "127.0.0.1", Port = port, Extension = "desk-main", RestoreOnStartup = true,
            MessagesEnabled = true, Extensions = [new("desk-extra", new() { AllowCalls = false, AllowSms = false })]
        };
        await store.SaveConnectionAsync(settings, "protected-primary-secret", true, new() { ["desk-extra"] = "protected-extra-secret" });
        var json = await File.ReadAllTextAsync(Path.Combine(directory, "sip.json"));
        Assert.DoesNotContain("protected-primary-secret", json); Assert.DoesNotContain("protected-extra-secret", json);
        await service.RestoreSipGatewayOnStartupAsync(store);
        Assert.True(service.GetSipGatewayStatus()?.IsRunning); Assert.Empty(service.Kernel.Pool.Slots);
        var model = new SettingsViewModel(service, store); await model.LoadSipSettingsAsync();
        Assert.True(model.SipMessagesEnabled); Assert.False(model.SipAllowOutgoingSms);
        var extra = Assert.Single(model.SipExtensions);
        Assert.Equal("desk-extra", extra.Username); Assert.Equal("protected-extra-secret", extra.Password);
        Assert.DoesNotContain("secret", extra.ToString());
        Assert.Equal(2, model.BuildSipGatewayOptions().Accounts.Count);
        Assert.False(model.BuildSipGatewayOptions().AllowCarrierCalls);
        await service.StopSipGatewayAsync();
        await store.SaveConnectionAsync((await store.LoadAsync()) with { RestoreOnStartup = false }, "", false);
        Assert.False(File.Exists(Path.Combine(directory, "sip.json.protected")));
    }

    [Fact]
    public async Task LegacyFingerprintAndDpapiPayloadRemainReadableWithAllNewFeaturesOff()
    {
        var settings = new SipGatewayPreferences { BindAddress = "127.0.0.1", RestoreOnStartup = true, WasEnabled = true,
            LineRoutes = new() { ["module-a"] = new() } };
        // Exact serialized v1 field order, including null/default old fields.
        var legacy = new
        {
            settings.UseTrunk, settings.BindAddress, settings.Port, settings.Realm, settings.Extension,
            settings.ServerAddress, settings.ServerPort, settings.TrunkUsername, settings.IncomingTarget,
            settings.AllowOutboundCalls, settings.RestoreOnStartup, settings.WasEnabled, settings.DefaultOutboundLineId,
            LineRoutes = new Dictionary<string, object> { ["module-a"] = new { Enabled = true,
                AllowIncomingCalls = true, AllowOutgoingCalls = true, IncomingTarget = (string?)null } }
        };
        var oldJson = JsonSerializer.Serialize(legacy);
        Assert.Equal(oldJson, JsonSerializer.Serialize(settings));
        var directory = Path.Combine(Path.GetTempPath(), "vowin-server-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "sip.json");
        await File.WriteAllTextAsync(path, oldJson);
        var secret = JsonSerializer.SerializeToUtf8Bytes(new { SettingsHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(oldJson))), Password = "v1-protected-secret" });
        await File.WriteAllBytesAsync(path + ".protected", SipStartupSecretProtection.Protect(secret));
        CryptographicOperations.ZeroMemory(secret);
        var store = new SipGatewaySettingsStore(path); var restored = await store.LoadAsync();
        Assert.Equal("v1-protected-secret", await store.LoadRememberedPasswordAsync(restored));
        Assert.Empty(await store.LoadRememberedExtensionsAsync(restored));
        Assert.False(restored.MessagesEnabled); Assert.False(restored.ForwardIncomingSms); Assert.False(restored.AllowOutgoingSms);
    }

    [Fact]
    public void DuplicateAccountsAndMissingSecretsFailClosedWhileRoutesAreUserDefined()
    {
        var settings = new SipGatewayPreferences { BindAddress = "127.0.0.1", Extension = "primary",
            Extensions = [new("extra", new() { AllowCalls = false })] };
        Assert.Throws<ArgumentException>(() => SipGatewayDeploymentProfile.BuildOptions(settings, "primary-secret"));
        var valid = SipGatewayDeploymentProfile.BuildOptions(settings, "primary-secret", new() { ["extra"] = "secondary-secret" });
        Assert.Equal(2, valid.Accounts.Count); Assert.Empty(valid.LineRoutes); Assert.Empty(valid.DialRules);
        Assert.False(valid.MessagesEnabled);
        Assert.Throws<ArgumentException>(() => SipGatewayDeploymentProfile.BuildOptions(settings with
            { Extensions = [new("primary", new())] }, "primary-secret"));
    }
}
