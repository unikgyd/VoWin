using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public sealed class EpdgSystemDnsTests
{
    [Fact]
    public void WindowsDnsAbiAndPolicyUseConfiguredServersAndFreshFqdnQueries()
    {
        Assert.Equal(IntPtr.Size == 8 ? 64 : 40, Marshal.SizeOf<WindowsEpdgDnsQuery.Request>());
        Assert.Equal(IntPtr.Size == 8 ? 32 : 24, Marshal.SizeOf<WindowsEpdgDnsQuery.Result>());
        Assert.Equal(IntPtr.Size * 2 + 16, Marshal.SizeOf<WindowsEpdgDnsQuery.RecordHeader>());
        var request = new WindowsEpdgDnsQuery.Request();
        Assert.Equal(0, request.Servers); // Windows DNS policy; never a hard-coded external resolver.
        Assert.Equal(0u, request.InterfaceIndex);
        Assert.Equal(0x1808UL, WindowsEpdgDnsQuery.QueryOptions);
    }

    private static nint Record(ushort type, IPAddress address, uint section = 1, ushort? length = null, nint next = 0)
    {
        var header = new WindowsEpdgDnsQuery.RecordHeader
        {
            Next = next, Type = type, DataLength = length ?? (ushort)address.GetAddressBytes().Length, Flags = section
        };
        var size = Marshal.SizeOf<WindowsEpdgDnsQuery.RecordHeader>();
        var record = Marshal.AllocHGlobal(size + 16);
        Marshal.StructureToPtr(header, record, false);
        var bytes = address.GetAddressBytes();
        Marshal.Copy(bytes, 0, record + size, bytes.Length);
        return record;
    }

    [Fact]
    public void ParsesOnlyRequestedAnswerAddressesAndPreservesNetworkByteOrder()
    {
        var v6 = Record(28, IPAddress.Parse("2001:db8::40"));
        var additional = Record(1, IPAddress.Parse("192.0.2.99"), section: 3, next: v6);
        var truncated = Record(1, IPAddress.Parse("192.0.2.88"), length: 2, next: additional);
        var answer = Record(1, IPAddress.Parse("192.0.2.40"), next: truncated);
        try
        {
            Assert.Equal(IPAddress.Parse("192.0.2.40"), Assert.Single(WindowsEpdgDnsQuery.ReadAddresses(answer, 1)));
            Assert.Equal(IPAddress.Parse("2001:db8::40"), Assert.Single(WindowsEpdgDnsQuery.ReadAddresses(answer, 28)));
            Marshal.WriteIntPtr(v6, answer);
            Assert.Throws<InvalidDataException>(() => WindowsEpdgDnsQuery.ReadAddresses(answer, 1));
        }
        finally { foreach (var record in new[] { v6, additional, truncated, answer }) Marshal.FreeHGlobal(record); }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("failure")]
    [InlineData("timeout")]
    public async Task BrokenIpv6CannotDiscardSuccessfulIpv4(string failure)
    {
        var blocked = new TaskCompletionSource<EpdgDnsFamilyResult>();
        var resolver = new SystemEpdgDnsResolver((_, type, _) => type == 1
            ? Task.FromResult(new EpdgDnsFamilyResult("A", [IPAddress.Parse("192.0.2.40")], "resolved"))
            : failure == "timeout" ? blocked.Task
            : failure == "failure" ? Task.FromException<EpdgDnsFamilyResult>(new SocketException((int)SocketError.HostNotFound))
            : Task.FromResult(new EpdgDnsFamilyResult("AAAA", [], "no-data")), TimeSpan.FromMilliseconds(50));
        Assert.Equal(IPAddress.Parse("192.0.2.40"), Assert.Single(await resolver.ResolveAsync("carrier.example.test", default)));
        Assert.Contains("A=resolved/1", resolver.Detail);
        Assert.Contains(failure == "timeout" ? "AAAA=timeout/0" : failure == "failure" ? "AAAA=dns-HostNotFound/0" : "AAAA=no-data/0", resolver.Detail);
        blocked.TrySetResult(new("AAAA", [], "late"));
    }

    [Fact]
    public async Task BrokenIpv4CannotDiscardSuccessfulIpv6()
    {
        var resolver = new SystemEpdgDnsResolver((_, type, _) => type == 28
            ? Task.FromResult(new EpdgDnsFamilyResult("AAAA", [IPAddress.Parse("2001:db8::40")], "resolved"))
            : Task.FromException<EpdgDnsFamilyResult>(new SocketException((int)SocketError.TryAgain)));
        Assert.Equal(IPAddress.Parse("2001:db8::40"), Assert.Single(await resolver.ResolveAsync("carrier.example.test", default)));
    }

    [Fact]
    public async Task FailedDiscoveryDoesNotCacheNegativeAnswersOrReuseOldAddresses()
    {
        var available = false;
        var calls = 0;
        var resolver = new SystemEpdgDnsResolver((_, type, _) =>
        {
            calls++;
            return Task.FromResult(new EpdgDnsFamilyResult(type == 1 ? "A" : "AAAA",
                available && type == 1 ? [IPAddress.Parse("192.0.2.40")] : [], "no-data"));
        });
        await Assert.ThrowsAsync<SocketException>(() => resolver.ResolveAsync("carrier.example.test", default));
        available = true;
        Assert.Single(await resolver.ResolveAsync("carrier.example.test", default));
        available = false;
        await Assert.ThrowsAsync<SocketException>(() => resolver.ResolveAsync("carrier.example.test", default));
        Assert.Equal(6, calls);
        Assert.All(resolver.Results, result => Assert.Empty(result.Addresses));
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedIntoDnsFailureAndLateLookupsAreHarmless()
    {
        var blocked = new TaskCompletionSource<EpdgDnsFamilyResult>();
        var queries = 0;
        var resolver = new SystemEpdgDnsResolver((_, _, _) => { queries++; return blocked.Task; });
        using var cancellation = new CancellationTokenSource();
        var pending = resolver.ResolveAsync("carrier.example.test", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(2, queries);
        blocked.SetResult(new("A", [], "late"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync("carrier.example.test", cancellation.Token));
        Assert.Equal(2, queries);
    }

    [Theory]
    [InlineData(VoWifiState.Failed, true, false, 1)]
    [InlineData(VoWifiState.Disconnected, true, false, 1)]
    [InlineData(VoWifiState.ImsRegistered, true, false, 0)]
    [InlineData(VoWifiState.Failed, false, false, 0)]
    [InlineData(VoWifiState.Failed, true, true, 0)]
    public void NetworkAddressChangeOnlyWakesEnabledOfflineRecovery(VoWifiState state, bool requested, bool paused, int expected)
    {
        using var manager = new VoWifiManager();
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(VoWifiManager).GetField("_keepOnlineRequested", fields)!.SetValue(manager, requested ? 1 : 0);
        typeof(VoWifiManager).GetProperty(nameof(manager.State))!.SetValue(manager, state);
        if (paused) typeof(VoWifiManager).GetField("_automaticRecoveryPauseReason", fields)!.SetValue(manager, "registrar pause");
        manager.NotifyNetworkPathChanged();
        manager.NotifyNetworkPathChanged(); // Coalesces, does not throw or flood startup.
        var wake = (SemaphoreSlim)typeof(VoWifiManager).GetField("_recoveryWake", fields)!.GetValue(manager)!;
        Assert.Equal(expected, wake.CurrentCount);
        Assert.Equal(state, manager.State);
    }

    [EpdgDnsLiveFact]
    public async Task WindowsFreshConfiguredDnsResolvesCarrierWithoutSimOrModem()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var resolver = new SystemEpdgDnsResolver();
        var addresses = await resolver.ResolveAsync("epdg.epc.mnc066.mcc515.pub.3gppnetwork.org", deadline.Token);
        Assert.Contains(addresses, address => address.AddressFamily == AddressFamily.InterNetwork);
        Assert.Contains("A=resolved/", resolver.Detail);
    }

    private sealed class EpdgDnsLiveFactAttribute : FactAttribute
    {
        public EpdgDnsLiveFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("VOWIN_TEST_EPDG_DNS") != "1")
                Skip = "Explicit configured-system DNS probe only; no SIM, modem, call or SMS.";
        }
    }
}
