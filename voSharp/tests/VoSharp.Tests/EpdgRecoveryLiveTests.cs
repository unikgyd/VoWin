using System.Net;
using System.Net.Sockets;
using VoSharp.Common.Events;
using VoSharp.Kernel.Pool;
using VoSharp.Telephony.VoWifi;
using Xunit.Abstractions;

namespace VoSharp.Tests;

[Collection("Windows modem lab")]
public sealed class EpdgRecoveryLiveTests(ITestOutputHelper output)
{
    private sealed class FirstDnsFailure : IEpdgDnsResolver
    {
        internal int Calls;
        internal SystemEpdgDnsResolver RealResolver { get; } = new();
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) =>
            Interlocked.Increment(ref Calls) == 1
                ? Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound))
                : RealResolver.ResolveAsync(host, ct);
    }

    [RecoveryLiveFact]
    public async Task Ec20RecoversFromInitialDnsFailureThroughRealIkeAndImsWithoutManualRestart()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var bus = new AsyncEventBus();
        using var subscription = bus.Subscribe(EventTopics.SystemLog, message =>
        {
            if (message.Source == "VoWiFi" && message.Payload?.ToString() is { } text &&
                (text.StartsWith("ePDG DNS:", StringComparison.Ordinal) ||
                 text.StartsWith("Starting automatic", StringComparison.Ordinal) ||
                 text.StartsWith("Automatic VoWiFi recovery succeeded", StringComparison.Ordinal)))
                output.WriteLine(text);
            return Task.CompletedTask;
        });
        // Only COM10 is attached. Never discover/open/restart the Host IMS module.
        await using var slot = new ModemSlot("ec20-dns-recovery", "COM10", eventBus: bus);
        Assert.True(await slot.AttachAsync(deadline.Token, initializeSms: false));
        Assert.Equal("515", slot.Sim?.Mcc);
        Assert.NotNull(slot.UsbContainerId);
        var dns = new FirstDnsFailure();
        slot.VoWifi.EpdgDiscovery = new(DnsResolver: dns);
        var registered = false;
        try
        {
            Assert.False(await slot.StartVoWifiAsync(ct: deadline.Token));
            Assert.Contains("DNS", slot.VoWifi.LastFailureCategory);
            Assert.True(slot.VoWifi.KeepOnlineRequested);
            // Observe the production automatic recovery worker, not a second manual Start.
            await slot.VoWifi.RecoveryCompletion.WaitAsync(TimeSpan.FromMinutes(3), deadline.Token);
            Assert.Equal(VoWifiState.ImsRegistered, slot.VoWifi.State);
            Assert.True(dns.Calls >= 2);
            Assert.NotNull(slot.VoWifi.ImsInfo);
            Assert.Null(slot.Calls.ActiveCall);
            registered = true;
            output.WriteLine($"Real EC20 automatic recovery accepted; DNS attempts={dns.Calls}; {dns.RealResolver.Detail}. Initial DNS failure was injected, not a reproduced natural outage.");
            await Task.Delay(TimeSpan.FromSeconds(25), deadline.Token);
            Assert.Equal(VoWifiState.ImsRegistered, slot.VoWifi.State);
            Assert.True((await slot.VoWifi.ProbeLivenessAsync(deadline.Token)).Success);
            output.WriteLine("Registration and IKE DPD remain healthy; no call, SMS, desktop audio or other modem access.");
        }
        finally
        {
            await slot.StopVoWifiAsync();
            output.WriteLine($"Cleanup: remote deregistration={slot.VoWifi.RemoteDeregistered}; state={slot.VoWifi.State}.");
            if (registered) Assert.True(slot.VoWifi.RemoteDeregistered, slot.VoWifi.LastDeregistrationError);
        }
    }

    private sealed class RecoveryLiveFactAttribute : FactAttribute
    {
        public RecoveryLiveFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("VOWIN_TEST_EPDG_RECOVERY") != "1" ||
                Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_SUBSCRIBER_EGRESS_APPROVED") != "1")
                Skip = "Explicit COM10-only carrier registration/recovery; no calls or SMS.";
        }
    }
}
