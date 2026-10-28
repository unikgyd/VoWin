using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using VoSharp.Kernel.SipGateway;
using VoWin.Services;

namespace VoWin.Tests;

// Actual output of the native Linux renderer/CLI, not a hand-written profile.
// It contains synthetic credentials only. The exact bytes are independently
// pinned to the source-hashed component-2 artifact; no service is started.
public sealed class SipDeploymentRendererImportTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task DeploymentProjectionActualLinuxRendererProfileIsConsumedByThePrivateWindowsReader(bool localEnabled, bool peerEnabled)
    {
        using var resource = typeof(SipDeploymentRendererImportTests).Assembly.GetManifestResourceStream("VoWin.Tests.Fixtures.SipDeploymentRendererV2.json");
        Assert.NotNull(resource);
        using var buffer = new MemoryStream();
        await resource.CopyToAsync(buffer);
        var bytes = buffer.ToArray();
        Assert.Equal("ff1743ca02b6d1b7fe89bf9721972fd6cd116335922d6e53583f2a0a0a1b753c", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        using var document = JsonDocument.Parse(bytes);
        Assert.True(document.RootElement.GetProperty("syntheticOnly").GetBoolean());
        var source = document.RootElement.GetProperty("fixtures").GetProperty("vowin-windows").EnumerateArray()
            .Single(item => item.GetProperty("localEnabled").GetBoolean() == localEnabled && item.GetProperty("peerEnabled").GetBoolean() == peerEnabled)
            .GetProperty("profile").GetRawText();
        var directory = Path.Combine(Path.GetTempPath(), "vowin-real-renderer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "private-input.json");
            await File.WriteAllTextAsync(path, source);
            var imported = await SipGatewayDeploymentProfile.ReadAsync(path);
            Assert.Equal(localEnabled, imported.Settings.HasLocalRegistrar);
            Assert.Equal(peerEnabled, imported.Settings.UseTrunk);
            Assert.False(imported.Settings.WasEnabled);
            Assert.False(imported.Settings.RestoreOnStartup);
            var options = SipGatewayDeploymentProfile.BuildOptions(imported.Settings, imported.Credentials, allowEmptyDraft: true);
            Assert.Equal(localEnabled ? 2 : 0, options.Accounts.Count);
            Assert.Equal(peerEnabled, options.Peer is not null);
            Assert.Equal("gateway.realm.invalid", options.Realm);
            Assert.Equal("messages.example.invalid", options.IdentityDomain);
            Assert.Equal("SHA-256", options.LocalRegistrarAlgorithm);
            Assert.Equal(new[] { "127.70.0.2/32" }, options.TrustedNetworks);
            Assert.Equal("synthetic-a", options.DefaultOutboundLineId);
            Assert.Equal("81", options.LineRoutes["synthetic-a"].Prefix);
            Assert.Equal("86", options.LineRoutes["synthetic-a"].CountryCode);
            Assert.True(options.LineRoutes["synthetic-a"].NormalizeE164);
            if (localEnabled)
            {
                Assert.False(options.ExtensionPolicies["desk-main"].AllowCalls);
                Assert.False(options.ExtensionPolicies["desk-main"].AllowInternalCalls);
                Assert.True(options.ExtensionPolicies["desk-main"].AllowMessages);
                Assert.Equal(new[] { "synthetic-b" }, options.ExtensionPolicies["desk-main"].SmsSendLines);
                Assert.Equal("synthetic-local-main-password", imported.Credentials.MainPassword);
            }
            else
            {
                Assert.Empty(imported.Credentials.MainPassword);
                Assert.NotNull(imported.Credentials.Extensions);
                Assert.Empty(imported.Credentials.Extensions);
                Assert.All(options.LineRoutes.Values, line => { Assert.Empty(line.LocalIncomingExtensions!); Assert.Empty(line.LocalSmsRecipients!); });
            }
            if (peerEnabled)
            {
                Assert.True(options.Peer!.AllowOutboundCalls);
                Assert.False(options.Peer.AllowOutboundMessages);
                Assert.False(options.Peer.AllowIncomingCalls);
                Assert.True(options.Peer.AllowIncomingMessages);
                Assert.Equal(new[] { "synthetic-a" }, options.Peer.OutgoingLines);
                Assert.Equal(new[] { "synthetic-b" }, options.Peer.SmsSendLines);
            }
            else
            {
                Assert.Empty(imported.Credentials.PeerInboundPassword);
                Assert.Empty(imported.Credentials.PeerOutboundPassword);
                Assert.All(options.LineRoutes.Values, line => { Assert.False(line.ForwardCallsToPeer); Assert.False(line.ForwardSmsToPeer); });
            }
            if (!localEnabled && !peerEnabled)
                Assert.Throws<ArgumentException>(() => SipGatewayDeploymentProfile.BuildOptions(imported.Settings, imported.Credentials));
            // The deployment envelope is deliberately PRIVATE. Only its
            // public settings are a credential-free diagnostic surface.
            var diagnostic = JsonSerializer.Serialize(imported.Settings);
            foreach (var secret in new[] { "synthetic-local-main-password", "synthetic-local-extra-password", "synthetic-peer-in-password", "synthetic-peer-out-password" })
                Assert.DoesNotContain(secret, diagnostic);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
