using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using System.Xml.Linq;
using VoSharp.HostIms;

namespace VoSharp.Tests;

[SupportedOSPlatform("windows")]
public sealed class ManagedHostImsOwnerTests
{
    private static readonly IPAddress Local = IPAddress.Parse("2001:db8::10");
    private static readonly IPAddress Pcscf = IPAddress.Parse("2001:db8::20");
    private static readonly IPAddress Media = IPAddress.Parse("2001:db8::30");
    private sealed class FakeRoutes : IRoutePlatform
    {
        public PppEndpoint Endpoint = new(72, Local, 9);
        public List<RouteRow> Rows = [new() { Index = 72, Luid = 9, Destination = SockaddrInet.From(IPAddress.IPv6Any),
            NextHop = SockaddrInet.From(IPAddress.Parse("fe80::1")), Metric = 10 }];
        public bool RejectVerify;
        public bool DefaultRoutesDisabled;
        public bool IgnoreDefaultPolicyWrite;
        public bool SuppressionRemovesDefault;
        public bool ThrowAfterDefaultSuppression;
        public bool IgnoreDelete;
        public List<bool> DefaultPolicyWrites = [];
        public int Checks;
        public PppEndpoint GetPpp() { Checks++; return Endpoint; }
        public IReadOnlyList<RouteRow> Read(uint index) => Rows.Where(r => r.Index == index).ToArray();
        public void Add(RouteRow row) => Rows.Add(row);
        public void Delete(RouteRow row)
        {
            if (!IgnoreDelete) Rows.RemoveAll(r => r.Index == row.Index && r.PrefixLength == row.PrefixLength && r.Destination.Address.Equals(row.Destination.Address));
        }
        public RouteRow HostRoute(RouteRow original, IPAddress address) { original.Destination = SockaddrInet.From(address); original.PrefixLength = 128; original.Metric = 1; return original; }
        public void Verify(PppEndpoint endpoint, IPAddress address) { if (RejectVerify && address.Equals(Media)) throw new IOException("verification rejected"); }
        public bool GetDefaultRoutesDisabled(PppEndpoint endpoint) => DefaultRoutesDisabled;
        public void SetDefaultRoutesDisabled(PppEndpoint endpoint, bool disabled)
        {
            DefaultPolicyWrites.Add(disabled);
            if (!IgnoreDefaultPolicyWrite) DefaultRoutesDisabled = disabled;
            if (disabled && SuppressionRemovesDefault) Rows.RemoveAll(r => r.Index == endpoint.Index && r.PrefixLength == 0);
            if (disabled && ThrowAfterDefaultSuppression) throw new IOException("native policy read-back failed");
        }
    }
    [Fact]
    public void WindowsAbiAndReadOnlyRouteEnumeration()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(104, Marshal.SizeOf<RouteRow>());
        Assert.Equal(44, Marshal.OffsetOf<RouteRow>(nameof(RouteRow.NextHop)).ToInt32());
        foreach (var row in WindowsRoutes.ReadAll())
        {
            Assert.Equal(23, row.Destination.Family); Assert.Equal(23, row.NextHop.Family);
            Assert.InRange(row.PrefixLength, (byte)0, (byte)128);
        }
        Assert.Equal(Media, SockaddrInet.From(Media).Address);
        var scoped = IPAddress.Parse("fe80::1%72");
        Assert.Equal(scoped, SockaddrInet.From(scoped).Address);
    }
    [Fact]
    public void RoutesAreIsolatedRecycledAndOriginalDefaultRestored()
    {
        var platform = new FakeRoutes();
        using (var routes = new RouteIsolation(platform, platform.Endpoint, [Pcscf]))
        {
            routes.Begin(); Assert.DoesNotContain(platform.Rows, r => r.PrefixLength == 0);
            for (var i = 0; i < 12; i++) { Assert.True(routes.AddMedia(Media)); routes.RemoveMedia(Media); }
            Assert.Equal(0, routes.MediaCount);
            Assert.True(routes.AddMedia(Pcscf)); Assert.Equal(0, routes.MediaCount);
            Assert.Throws<InvalidOperationException>(() => routes.RemoveMedia(Pcscf));
        }
        Assert.Single(platform.Rows); Assert.Equal(0, platform.Rows[0].PrefixLength); Assert.Equal(10u, platform.Rows[0].Metric);
    }
    [Theory]
    [InlineData("::")][InlineData("::1")][InlineData("fe80::1")][InlineData("ff02::1")][InlineData("192.0.2.1")][InlineData("::ffff:192.0.2.1")][InlineData("2001:db8::10")]
    public void UnsafeMediaDestinationsAreRejected(string address)
    {
        var platform = new FakeRoutes(); using var routes = new RouteIsolation(platform, platform.Endpoint, [Pcscf]); routes.Begin();
        Assert.False(routes.AddMedia(IPAddress.Parse(address))); Assert.Equal(0, routes.MediaCount);
    }
    [Fact]
    public void MediaLimitAndFailedVerificationDoNotLeakRoutes()
    {
        var platform = new FakeRoutes { RejectVerify = true }; using var routes = new RouteIsolation(platform, platform.Endpoint, [Pcscf]); routes.Begin();
        Assert.False(routes.AddMedia(Media)); Assert.Single(platform.Rows);
        for (var i = 1; i <= 8; i++) Assert.True(routes.AddMedia(IPAddress.Parse("2001:db8:1::" + i)));
        Assert.False(routes.AddMedia(IPAddress.Parse("2001:db8:1::9")));
        Assert.Equal(8, routes.MediaCount);
    }
    [Fact]
    public void ExistingRoutesAreNeverAdoptedOrDeleted()
    {
        var platform = new FakeRoutes(); var external = platform.HostRoute(platform.Rows[0], Pcscf); platform.Add(external);
        using (var routes = new RouteIsolation(platform, platform.Endpoint, [Pcscf])) Assert.Throws<InvalidOperationException>(routes.Begin);
        Assert.Equal(2, platform.Rows.Count);
    }
    [Fact]
    public void IsolationLossIsFatalRatherThanRouteRejection()
    {
        var platform = new FakeRoutes(); using var routes = new RouteIsolation(platform, platform.Endpoint, [Pcscf]); routes.Begin();
        platform.Rows.Add(new() { Index = 72, Luid = 9, Destination = SockaddrInet.From(IPAddress.IPv6Any), NextHop = SockaddrInet.From(IPAddress.Parse("fe80::1")), Metric = 11 });
        Assert.Throws<InvalidOperationException>(() => routes.AddMedia(Media));
        platform.Rows.RemoveAll(r => r.PrefixLength == 0); // Leave cleanup able to restore only the owned original.
    }
    [Fact]
    public async Task ExactOriginalDefaultMayBeReisolatedWhileNativePolicyBlocksItsUse()
    {
        var platform = new FakeRoutes(); var original = platform.Rows.Single();
        using var routes = new RouteIsolation(platform, platform.Endpoint, [Pcscf]); routes.Begin();
        for (var i = 0; i < 3; i++)
        {
            var refreshed = original; refreshed.Age = (uint)(i * 120); refreshed.ValidLifetime = 3600;
            platform.Rows.Add(refreshed); routes.Check();
            Assert.True(platform.DefaultRoutesDisabled);
            Assert.DoesNotContain(platform.Rows, r => r.PrefixLength == 0);
        }
        Assert.Equal(3, routes.DefaultRouteRepairCount);
        var input = Channel.CreateUnbounded<string>(); input.Writer.Complete(); using var output = new StringWriter();
        await LeaseControl.Run(routes, platform.Endpoint, [Pcscf], input.Reader, output,
            () => false, () => Task.CompletedTask, TimeSpan.FromSeconds(10), default);
        Assert.Contains("VOWIN_IMS_DEFAULT_ROUTE_REISOLATED count=3", output.ToString());
    }
    [Fact]
    public void FailedDefaultDeletionOrLostSuppressionNeverReportsSuccessfulRepair()
    {
        var platform = new FakeRoutes(); var original = platform.Rows.Single();
        using var routes = new RouteIsolation(platform, platform.Endpoint, [Pcscf]); routes.Begin();
        platform.Rows.Add(original); platform.IgnoreDelete = true;
        Assert.Throws<InvalidOperationException>(routes.Check);
        Assert.Equal(0, routes.DefaultRouteRepairCount);
        platform.IgnoreDelete = false; platform.DefaultRoutesDisabled = false;
        Assert.Throws<InvalidOperationException>(routes.Check);
        Assert.Contains(platform.Rows, r => r.PrefixLength == 0);
        Assert.Equal(0, routes.DefaultRouteRepairCount);
    }
    [Fact]
    public void DefaultSuppressionIsOwnedVerifiedAndRestored()
    {
        var platform = new FakeRoutes();
        using (var routes = new RouteIsolation(platform, platform.Endpoint, [Pcscf]))
        {
            routes.Begin(); Assert.True(platform.DefaultRoutesDisabled);
            Assert.Equal([true], platform.DefaultPolicyWrites);
            for (var i = 0; i < 1000; i++) routes.Check();
        }
        Assert.False(platform.DefaultRoutesDisabled);
        Assert.Equal([true, false], platform.DefaultPolicyWrites);
        Assert.Single(platform.Rows);
    }
    [Fact]
    public void ExistingDefaultSuppressionIsNeverOverwrittenOrReverted()
    {
        var platform = new FakeRoutes { DefaultRoutesDisabled = true };
        using (var routes = new RouteIsolation(platform, platform.Endpoint, [Pcscf])) routes.Begin();
        Assert.True(platform.DefaultRoutesDisabled); Assert.Empty(platform.DefaultPolicyWrites);
    }
    [Fact]
    public void NativePolicyMayRemoveDefaultBeforeExplicitDeletion()
    {
        var platform = new FakeRoutes { SuppressionRemovesDefault = true };
        using (var routes = new RouteIsolation(platform, platform.Endpoint, [Pcscf]))
        { routes.Begin(); Assert.DoesNotContain(platform.Rows, r => r.PrefixLength == 0); }
        Assert.Single(platform.Rows); Assert.False(platform.DefaultRoutesDisabled);
    }
    [Fact]
    public void UnconfirmedDefaultSuppressionRollsBackWithoutReportingReady()
    {
        var platform = new FakeRoutes { IgnoreDefaultPolicyWrite = true };
        using (var routes = new RouteIsolation(platform, platform.Endpoint, [Pcscf]))
            Assert.Throws<InvalidOperationException>(routes.Begin);
        Assert.Single(platform.Rows); Assert.Equal(0, platform.Rows[0].PrefixLength);
    }
    [Fact]
    public void PartiallyAppliedDefaultSuppressionRestoresTheOriginalDefault()
    {
        var platform = new FakeRoutes { SuppressionRemovesDefault = true, ThrowAfterDefaultSuppression = true };
        using (var routes = new RouteIsolation(platform, platform.Endpoint, [Pcscf]))
            Assert.Throws<IOException>(routes.Begin);
        Assert.Equal([true, false], platform.DefaultPolicyWrites);
        Assert.False(platform.DefaultRoutesDisabled);
        Assert.Single(platform.Rows); Assert.Equal(0, platform.Rows[0].PrefixLength);
    }
    [Fact]
    public void PolicyLossAndReusedInterfaceLuidRemainFatalAndDoNotTouchReplacement()
    {
        var platform = new FakeRoutes(); var routes = new RouteIsolation(platform, platform.Endpoint, [Pcscf]); routes.Begin();
        platform.DefaultRoutesDisabled = false;
        Assert.Throws<InvalidOperationException>(routes.Check);
        platform.Endpoint = platform.Endpoint with { Luid = 10 };
        routes.Dispose();
        Assert.Equal([true], platform.DefaultPolicyWrites); // No restore into a reused interface index.
        Assert.DoesNotContain(platform.Rows, r => r.PrefixLength == 0);
    }
    [Fact]
    public void ChangedRouteIsNotRemovedDuringCleanup()
    {
        var platform = new FakeRoutes(); var routes = new RouteIsolation(platform, platform.Endpoint, [Pcscf]); routes.Begin(); routes.AddMedia(Media);
        var index = platform.Rows.FindIndex(r => r.Destination.Address.Equals(Media));
        var changed = platform.Rows[index]; changed.Metric = 42; platform.Rows[index] = changed;
        Assert.Throws<InvalidOperationException>(() => routes.RemoveMedia(Media));
        Assert.Throws<AggregateException>(routes.Dispose);
        Assert.Contains(platform.Rows, r => r.Destination.Address.Equals(Media) && r.Metric == 42);
    }
    [Fact]
    public void RouteReplacedWithSameKeyButDifferentOriginIsNotOwned()
    {
        var platform = new FakeRoutes(); var routes = new RouteIsolation(platform, platform.Endpoint, [Pcscf]); routes.Begin();
        var index = platform.Rows.FindIndex(r => r.Destination.Address.Equals(Pcscf));
        var replacement = platform.Rows[index]; replacement.Origin++; platform.Rows[index] = replacement;
        Assert.Throws<InvalidOperationException>(routes.Check);
        Assert.Throws<AggregateException>(routes.Dispose);
        Assert.Contains(platform.Rows, r => r.Destination.Address.Equals(Pcscf) && r.Origin == replacement.Origin);
    }
    [Fact]
    public async Task ManagedPipeProcessesRenewAddRemoveAndEof()
    {
        var platform = new FakeRoutes(); using var routes = new RouteIsolation(platform, platform.Endpoint, [Pcscf]); routes.Begin();
        var input = Channel.CreateUnbounded<string>(); using var output = new StringWriter(); var renewals = 0;
        foreach (var command in new[] { "RENEW", "ROUTE ADD " + Media, "ROUTE REMOVE " + Media }) input.Writer.TryWrite(command);
        input.Writer.Complete();
        await LeaseControl.Run(routes, platform.Endpoint, [Pcscf], input.Reader, output, () => false,
            () => { renewals++; return Task.CompletedTask; }, TimeSpan.FromSeconds(10), default);
        Assert.Equal(1, renewals); Assert.Equal(0, routes.MediaCount);
        Assert.Contains("VOWIN_IMS_BEARER_READY", output.ToString());
        Assert.Contains("\"InterfaceLuid\":9", output.ToString());
        Assert.Contains("VOWIN_IMS_LEASE_RENEWED", output.ToString());
        Assert.Contains("VOWIN_IMS_MEDIA_ROUTE_ADDED " + Media, output.ToString());
        Assert.Contains("VOWIN_IMS_MEDIA_ROUTE_REMOVED " + Media, output.ToString());
    }
    [Fact]
    public async Task IdlePipeDoesNotBlockRouteChecksAndExpires()
    {
        var platform = new FakeRoutes(); using var routes = new RouteIsolation(platform, platform.Endpoint, [Pcscf]); routes.Begin();
        var input = Channel.CreateUnbounded<string>(); using var output = new StringWriter();
        await Assert.ThrowsAsync<TimeoutException>(() => LeaseControl.Run(routes, platform.Endpoint, [Pcscf], input.Reader, output,
            () => false, () => Task.CompletedTask, TimeSpan.FromMilliseconds(150), default, TimeSpan.FromMilliseconds(20)));
        Assert.True(platform.Checks >= 3);
    }
    [Fact]
    public async Task RenewFailureDoesNotAcknowledgeAndUnexpectedCommandsFailClosed()
    {
        var platform = new FakeRoutes(); using var routes = new RouteIsolation(platform, platform.Endpoint, [Pcscf]); routes.Begin();
        var input = Channel.CreateUnbounded<string>(); using var output = new StringWriter(); input.Writer.TryWrite("RENEW");
        await Assert.ThrowsAsync<IOException>(() => LeaseControl.Run(routes, platform.Endpoint, [Pcscf], input.Reader, output,
            () => false, () => throw new IOException("RAS gone"), TimeSpan.FromSeconds(10), default));
        Assert.DoesNotContain("VOWIN_IMS_LEASE_RENEWED", output.ToString());
        input.Writer.TryWrite("DELETE EVERYTHING");
        await Assert.ThrowsAsync<InvalidDataException>(() => LeaseControl.Run(routes, platform.Endpoint, [Pcscf], input.Reader, output,
            () => false, () => Task.CompletedTask, TimeSpan.FromSeconds(10), default));
    }
    [Fact]
    public void TaskXmlQuotesUnicodePathAndNeverUsesPowerShell()
    {
        var xml = RecoveryGuard.BuildTaskXml(@"C:\Program Files\VoWin\测试 & runtime\VoSharp.HostIms.exe", @"C:\ProgramData\VoWin.ManagedImsRecovery\abc.json");
        var doc = XDocument.Parse(xml); XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal(@"C:\Program Files\VoWin\测试 & runtime\VoSharp.HostIms.exe", doc.Descendants(ns + "Command").Single().Value);
        Assert.Equal("--recover-boot \"C:\\ProgramData\\VoWin.ManagedImsRecovery\\abc.json\"", doc.Descendants(ns + "Arguments").Single().Value);
        Assert.DoesNotContain("powershell", xml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("S-1-5-18", xml);
    }
    [Fact]
    public void PcscfParserValidatesImsContextAndSharedPrefix()
    {
        static string Dotted(string ip) => string.Join('.', IPAddress.Parse(ip).GetAddressBytes());
        var response = "+CGCONTRDP: 5,5,\"ims\",\"" + Dotted("2001:db8::11") + "\",\"\",\"\",\"\",\"" + Dotted(Pcscf.ToString()) + "\",\"" + Dotted(Pcscf.ToString()) + "\"\r\nOK\r\n";
        Assert.Equal([Pcscf], ModemControl.ParsePcscf(response, Local));
        Assert.Throws<InvalidDataException>(() => ModemControl.ParsePcscf(response.Replace("\"ims\"", "\"internet\""), Local));
        Assert.Throws<InvalidDataException>(() => ModemControl.ParsePcscf(response, IPAddress.Parse("2001:db9::10")));
        Assert.Throws<InvalidDataException>(() => ModemControl.ParseDottedIpv6("1.2.3"));
        Assert.Throws<InvalidDataException>(() => ModemControl.ParseDottedIpv6(string.Join('.', Enumerable.Repeat("256", 16))));
    }
}
