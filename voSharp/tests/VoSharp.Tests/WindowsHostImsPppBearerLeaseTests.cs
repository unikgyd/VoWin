using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using VoSharp.Kernel.Pool;
using VoSharp.Modem;
using VoSharp.Sip;
using VoSharp.Telephony;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

[Collection("Windows modem lab")]
public sealed class WindowsHostImsPppBearerLeaseTests
{
    private static ProcessStartInfo FakeOwnerStart(bool wrongAcknowledgement,
        bool exitOnRoute = false, bool exitOnRenew = false, bool rejectRoute = false,
        bool loseBearerOnRoute = false, bool wrongRemoveAcknowledgement = false, bool reportRepairs = false,
        string? stopBarrier = null, string? routeBarrier = null, bool reportLuid = false)
    {
        const string script = """
            $ready = '{"InterfaceIndex":72,"LocalAddress":"2001:db8::10","PcscfAddresses":["2001:db8::20"]}'
            if ($env:VOWIN_TEST_REPORT_LUID -eq '1') { $ready = $ready.Replace('"InterfaceIndex":72', '"InterfaceIndex":72,"InterfaceLuid":720') }
            [Console]::Out.WriteLine("VOWIN_IMS_BEARER_READY $ready")
            while ($true) {
                $line = [Console]::In.ReadLine()
                if ($null -eq $line -or $line -eq 'STOP') {
                    if ($env:VOWIN_TEST_STOP_BARRIER) {
                        [IO.File]::WriteAllText(($env:VOWIN_TEST_STOP_BARRIER + '.stopped'), 'STOP')
                        while (-not [IO.File]::Exists($env:VOWIN_TEST_STOP_BARRIER)) { Start-Sleep -Milliseconds 20 }
                    }
                    exit 0
                }
                if ($line -match '^ROUTE ADD ([0-9A-Fa-f:]+)$') {
                    if ($env:VOWIN_TEST_EXIT_ON_ROUTE -eq '1') { exit 4 }
                    if ($env:VOWIN_TEST_LOSE_ON_ROUTE -eq '1') {
                        [Console]::Out.WriteLine('VOWIN_IMS_BEARER_LOST')
                    }
                    elseif ($env:VOWIN_TEST_REJECT_ROUTE -eq '1') {
                        [Console]::Out.WriteLine('VOWIN_IMS_MEDIA_ROUTE_REJECTED')
                    }
                    else {
                        $address = if ($env:VOWIN_TEST_WRONG_ACK -eq '1') { '2001:db8::99' } else { $Matches[1] }
                        if ($env:VOWIN_TEST_ROUTE_BARRIER) {
                            # The route exists, but its acknowledgement has not reached the client.
                            [IO.File]::WriteAllText(($env:VOWIN_TEST_ROUTE_BARRIER + '.added'), $address)
                            while (-not [IO.File]::Exists($env:VOWIN_TEST_ROUTE_BARRIER)) { Start-Sleep -Milliseconds 20 }
                        }
                        [Console]::Out.WriteLine("VOWIN_IMS_MEDIA_ROUTE_ADDED $address")
                    }
                }
                elseif ($line -match '^ROUTE REMOVE ([0-9A-Fa-f:]+)$') {
                    $address = if ($env:VOWIN_TEST_WRONG_REMOVE_ACK -eq '1') { '2001:db8::99' } else { $Matches[1] }
                    [Console]::Out.WriteLine("VOWIN_IMS_MEDIA_ROUTE_REMOVED $address")
                }
                elseif ($line -eq 'RENEW') {
                    if ($env:VOWIN_TEST_EXIT_ON_RENEW -eq '1') { exit 5 }
                    if ($env:VOWIN_TEST_REPORT_REPAIRS -eq '1') {
                        [Console]::Error.WriteLine('VOWIN_IMS_DEFAULT_ROUTE_REISOLATED count=999')
                        [Console]::Out.WriteLine('VOWIN_IMS_DEFAULT_ROUTE_REISOLATED count=3')
                        [Console]::Out.WriteLine('VOWIN_IMS_DEFAULT_ROUTE_REISOLATED count=2')
                        [Console]::Out.WriteLine('VOWIN_IMS_DEFAULT_ROUTE_REISOLATED count=invalid')
                    }
                    [Console]::Out.WriteLine('VOWIN_IMS_LEASE_RENEWED')
                }
                else { exit 2 }
            }
            """;
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        start.Environment["VOWIN_TEST_WRONG_ACK"] = wrongAcknowledgement ? "1" : "0";
        start.Environment["VOWIN_TEST_EXIT_ON_ROUTE"] = exitOnRoute ? "1" : "0";
        start.Environment["VOWIN_TEST_EXIT_ON_RENEW"] = exitOnRenew ? "1" : "0";
        start.Environment["VOWIN_TEST_REJECT_ROUTE"] = rejectRoute ? "1" : "0";
        start.Environment["VOWIN_TEST_LOSE_ON_ROUTE"] = loseBearerOnRoute ? "1" : "0";
        start.Environment["VOWIN_TEST_WRONG_REMOVE_ACK"] = wrongRemoveAcknowledgement ? "1" : "0";
        start.Environment["VOWIN_TEST_REPORT_REPAIRS"] = reportRepairs ? "1" : "0";
        start.Environment["VOWIN_TEST_REPORT_LUID"] = reportLuid ? "1" : "0";
        if (stopBarrier is not null) start.Environment["VOWIN_TEST_STOP_BARRIER"] = stopBarrier;
        if (routeBarrier is not null) start.Environment["VOWIN_TEST_ROUTE_BARRIER"] = routeBarrier;
        return start;
    }

    private sealed class StopBarrier : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "vowin-fake-owner-" + Guid.NewGuid().ToString("N"));
        public string PathName => Path.Combine(_directory, "release");
        public StopBarrier() => Directory.CreateDirectory(_directory);
        public void Release() => File.WriteAllText(PathName, "release fake owner");
        public async Task WaitForStopAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(PathName + ".stopped")) await Task.Delay(20, deadline.Token);
        }
        public async Task WaitForRouteAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(PathName + ".added")) await Task.Delay(20, deadline.Token);
        }
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task FakePppOwnerIdentityReachesMediaAndCannotBeDroppedDuringRegistration()
    {
        if (!OperatingSystem.IsWindows()) return;
        var destination = IPAddress.Parse("2001:db8::30");
        WindowsHostImsPppBearerInfo? verifiedOwner = null;
        await using var lease = await WindowsHostImsPppBearerLease.StartProtocolTestOwnerAsync(
            FakeOwnerStart(false, reportLuid: true),
            (owner, target) => { verifiedOwner = owner; return target.Equals(destination); });
        await using var slot = new ModemSlot("fake-owned-ims", "COM999");
        var field = typeof(ModemSlot).GetField("_hostImsPppLease", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True(slot.IsHostImsPppOwnerCurrent(null));
        field.SetValue(slot, lease);
        try
        {
            Assert.True(slot.IsHostImsPppOwnerCurrent(lease));
            Assert.False(slot.IsHostImsPppOwnerCurrent(null));
            await lease.EnsureMediaRouteAsync(destination);
            Assert.Equal(72, verifiedOwner!.InterfaceIndex);
            Assert.Equal(720ul, verifiedOwner.InterfaceLuid);
            field.SetValue(slot, null); // Loss during a probe cannot turn this into an unowned WWAN registration.
            Assert.False(slot.IsHostImsPppOwnerCurrent(lease));
            field.SetValue(slot, lease);
            await lease.DisposeAsync();
            Assert.False(slot.IsHostImsPppOwnerCurrent(lease));
        }
        finally { field.SetValue(slot, null); }
    }

    [Fact]
    public async Task ApplicationExitSendsStopButDoesNotWaitForIndependentRestoration()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var barrier = new StopBarrier();
        var lease = await WindowsHostImsPppBearerLease.StartProtocolTestOwnerAsync(
            FakeOwnerStart(false, stopBarrier: barrier.PathName), (_, _) => true);
        try
        {
            lease.AllowBackgroundCleanupOnApplicationExit();
            await lease.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await barrier.WaitForStopAsync();
            Assert.False(lease.Completion.IsCompleted);
            await lease.DisposeAsync(); // Repeated desktop disposal cannot stop the independent cleanup.
        }
        finally { barrier.Release(); await lease.Completion.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.Equal(0, await lease.Completion);
    }

    [Fact]
    public async Task ExplicitStopWaitsForRestorationUntilApplicationExitTakesOver()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var barrier = new StopBarrier();
        var lease = await WindowsHostImsPppBearerLease.StartProtocolTestOwnerAsync(
            FakeOwnerStart(false, stopBarrier: barrier.PathName), (_, _) => true);
        var stopping = lease.DisposeAsync().AsTask();
        try
        {
            await barrier.WaitForStopAsync();
            Assert.False(stopping.IsCompleted); // Normal stop retains its previous contract.
            lease.AllowBackgroundCleanupOnApplicationExit();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(lease.Completion.IsCompleted);
        }
        finally { barrier.Release(); await lease.Completion.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task SlotExitAlsoDetachesAnAlreadyRemovedStoppingLease()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var barrier = new StopBarrier();
        var lease = await WindowsHostImsPppBearerLease.StartProtocolTestOwnerAsync(
            FakeOwnerStart(false, stopBarrier: barrier.PathName), (_, _) => true);
        await using var slot = new ModemSlot("exit-race", "COM999");
        typeof(ModemSlot).GetField("_hostImsPppLease", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(slot, lease);
        var stopping = slot.StopHostImsPppBearerAsync();
        try
        {
            await barrier.WaitForStopAsync();
            Assert.False(slot.GetHostImsPppBearerStatus().IsActive);
            Assert.False(stopping.IsCompleted);
            slot.PrepareForApplicationExit();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            await slot.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(lease.Completion.IsCompleted);
        }
        finally { barrier.Release(); await lease.Completion.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task DefaultRouteRepairDiagnosticsAreMonotonicAndOnlyReadFromStdout()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var lease = await WindowsHostImsPppBearerLease.StartProtocolTestOwnerAsync(
            FakeOwnerStart(wrongAcknowledgement: false, reportRepairs: true), (_, _) => true);
        Assert.Equal(0, lease.DefaultRouteRepairCount);
        await lease.RenewLeaseAsync();
        Assert.Equal(3, lease.DefaultRouteRepairCount);
        await using var slot = new ModemSlot("repair-test", "COM999");
        typeof(ModemSlot).GetField("_hostImsPppLease", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(slot, lease);
        Assert.Equal(3, slot.GetHostImsPppBearerStatus().DefaultRouteRepairCount);
        typeof(ModemSlot).GetField("_hostImsPppLease", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(slot, null);
    }

    [Fact]
    public async Task FakePppOwnerHeartbeatsBeyondInitialLeaseAndStops()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var lease = await WindowsHostImsPppBearerLease.StartProtocolTestOwnerAsync(
            FakeOwnerStart(wrongAcknowledgement: false), (_, _) => true,
            heartbeatLeaseSeconds: 15);
        Assert.Equal(0, lease.RenewalCount);
        Assert.Null(lease.LastRenewedAtUtc);
        await Task.Delay(TimeSpan.FromSeconds(7));
        Assert.True(lease.RenewalCount >= 1);
        var firstRenewedAt = Assert.IsType<DateTime>(lease.LastRenewedAtUtc);
        Assert.Equal(DateTimeKind.Utc, firstRenewedAt.Kind);
        await lease.RenewLeaseAsync();
        Assert.True(lease.RenewalCount >= 2);
        Assert.True(lease.LastRenewedAtUtc >= firstRenewedAt);
        await lease.DisposeAsync();
        Assert.Equal(0, await lease.Completion);
    }

    [Fact]
    public async Task FakePppOwnerExitDuringRenewUnblocksCaller()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var lease = await WindowsHostImsPppBearerLease.StartProtocolTestOwnerAsync(
            FakeOwnerStart(wrongAcknowledgement: false, exitOnRenew: true), (_, _) => true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.RenewLeaseAsync());
        Assert.Equal(5, await lease.Completion);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await lease.DisposeAsync());
    }

    private static HostImsRegistrationClient FakeExpiredHostImsClient()
    {
        var local = IPAddress.Parse("2001:db8::10");
        var pcscf = IPAddress.Parse("2001:db8::20");
        var transport = new SipTransport();
        var profile = new ImsProfile("001010123456789@ims.test",
            "sip:001010123456789@ims.test", "ims.test", "123456789012345",
            local.ToString(), 40000);
        var registration = new SipRegisterSession(transport, profile);
        var result = new SipRegistrationResult("<sip:lab@[2001:db8::10]>",
            60, null, null, DateTime.UtcNow);
        Func<byte[], byte[], CancellationToken,
            Task<(byte[] Res, byte[] Ck, byte[] Ik)>> aka = (_, _, _) =>
            Task.FromResult((Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>()));
        var constructor = typeof(HostImsRegistrationClient)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        return (HostImsRegistrationClient)constructor.Invoke(
            [new HostImsEndpointCandidate(5, local, pcscf), transport,
                registration, result, 7, aka, null, null]);
    }

    [Fact]
    public async Task FakePppOwnerAcknowledgesExactMediaRouteAndStops()
    {
        if (!OperatingSystem.IsWindows()) return;
        var destination = IPAddress.Parse("2001:db8::30");
        var routeChecks = 0;
        await using var lease = await WindowsHostImsPppBearerLease.StartProtocolTestOwnerAsync(
            FakeOwnerStart(wrongAcknowledgement: false),
            (bearer, address) =>
            {
                Interlocked.Increment(ref routeChecks);
                return bearer.InterfaceIndex == 72 && address.Equals(destination);
            });
        Assert.NotNull(lease.Bearer);
        await lease.EnsureMediaRouteAsync(destination);
        await lease.EnsureMediaRouteAsync(destination); // Repeat verifies, but sends no duplicate command.
        Assert.Equal(2, routeChecks);
        Assert.Equal(1, lease.MediaRouteCount);
        await lease.ClearMediaRoutesAsync();
        Assert.Equal(0, lease.MediaRouteCount);
        await lease.ClearMediaRoutesAsync(); // Empty cleanup is idempotent.
        await lease.EnsureMediaRouteAsync(destination); // A later call may reuse the address.
        Assert.Equal(1, lease.MediaRouteCount);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            lease.EnsureMediaRouteAsync(IPAddress.IPv6Loopback));
        await lease.DisposeAsync();
        Assert.Equal(0, await lease.Completion);
    }

    [Fact]
    public async Task FakePppOwnerCancelledMediaAddFencesBearerBeforeLateAcknowledgement()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var barrier = new StopBarrier();
        var destination = IPAddress.Parse("2001:db8::30");
        var lease = await WindowsHostImsPppBearerLease.StartProtocolTestOwnerAsync(
            FakeOwnerStart(false, routeBarrier: barrier.PathName), (_, _) => true);
        try
        {
            using var cancel = new CancellationTokenSource();
            var adding = lease.EnsureMediaRouteAsync(destination, cancel.Token);
            await barrier.WaitForRouteAsync();
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adding)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(lease.IsLost); // Fence immediately, without waiting for owner exit or restoration.
            Assert.Equal(0, lease.MediaRouteCount);

            // Deliver the exact late frame through the real parser, even if the owned
            // process was already killed. It must never revive the fenced session.
            using var lateFrame = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(
                $"VOWIN_IMS_MEDIA_ROUTE_ADDED {destination}\n")));
            var pump = typeof(WindowsHostImsPppBearerLease).GetMethod("PumpAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)pump.Invoke(lease, [lateFrame, true])!;
            Assert.True(lease.IsLost);
            Assert.Equal(0, lease.MediaRouteCount);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                lease.EnsureMediaRouteAsync(IPAddress.Parse("2001:db8::31")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => lease.ClearMediaRoutesAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => lease.RenewLeaseAsync());
            Assert.NotEqual(0, await lease.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            barrier.Release();
            try { await lease.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (InvalidOperationException) when (lease.IsLost) { }
        }
    }

    [Fact]
    public async Task FakePppOwnerPostAddVerificationFailureFencesAndAbortsBearer()
    {
        if (!OperatingSystem.IsWindows()) return;
        var lease = await WindowsHostImsPppBearerLease.StartProtocolTestOwnerAsync(
            FakeOwnerStart(false), (_, _) => false);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                lease.EnsureMediaRouteAsync(IPAddress.Parse("2001:db8::30")));
            Assert.True(lease.IsLost);
            await Assert.ThrowsAsync<InvalidOperationException>(() => lease.RenewLeaseAsync());
            Assert.NotEqual(0, await lease.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            try { await lease.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (InvalidOperationException) when (lease.IsLost) { }
        }
    }

    [Fact]
    public async Task FakePppOwnerTimedOutMediaAddFencesAndAbortsBearer()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var barrier = new StopBarrier();
        var lease = await WindowsHostImsPppBearerLease.StartProtocolTestOwnerAsync(
            FakeOwnerStart(false, routeBarrier: barrier.PathName), (_, _) => true,
            mediaRouteAcknowledgementTimeout: TimeSpan.FromSeconds(1));
        try
        {
            var adding = lease.EnsureMediaRouteAsync(IPAddress.Parse("2001:db8::30"));
            await barrier.WaitForRouteAsync();
            await Assert.ThrowsAsync<TimeoutException>(() => adding)
                .WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(lease.IsLost);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                lease.EnsureMediaRouteAsync(IPAddress.Parse("2001:db8::31")));
            Assert.NotEqual(0, await lease.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            barrier.Release();
            try { await lease.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (InvalidOperationException) when (lease.IsLost) { }
        }
    }

    [Fact]
    public async Task FakePppOwnerCancellationBeforeMediaCommandKeepsBearerUsable()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var lease = await WindowsHostImsPppBearerLease.StartProtocolTestOwnerAsync(
            FakeOwnerStart(false), (_, _) => true);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            lease.EnsureMediaRouteAsync(IPAddress.Parse("2001:db8::30"), cancel.Token));
        Assert.False(lease.IsLost);
        await lease.EnsureMediaRouteAsync(IPAddress.Parse("2001:db8::31"));
        Assert.Equal(1, lease.MediaRouteCount);
        await lease.ClearMediaRoutesAsync();
        await lease.RenewLeaseAsync();
    }

    [Fact]
    public async Task FakePppOwnerWrongRemoveAcknowledgementAbortsBearer()
    {
        if (!OperatingSystem.IsWindows()) return;
        var lease = await WindowsHostImsPppBearerLease.StartProtocolTestOwnerAsync(
            FakeOwnerStart(wrongAcknowledgement: false, wrongRemoveAcknowledgement: true),
            (_, _) => true);
        await lease.EnsureMediaRouteAsync(IPAddress.Parse("2001:db8::30"));
        await Assert.ThrowsAsync<FormatException>(() => lease.ClearMediaRoutesAsync());
        Assert.True(lease.IsLost);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await lease.DisposeAsync());
    }

    [Fact]
    public async Task FakePppOwnerRecyclesMediaRoutesAcrossMoreThanEightCalls()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var lease = await WindowsHostImsPppBearerLease.StartProtocolTestOwnerAsync(
            FakeOwnerStart(wrongAcknowledgement: false), (_, _) => true);
        for (var call = 0; call < 12; call++)
        {
            await lease.EnsureMediaRouteAsync(IPAddress.Parse($"2001:db8::{call + 30}"));
            Assert.Equal(1, lease.MediaRouteCount);
            await lease.ClearMediaRoutesAsync();
            Assert.Equal(0, lease.MediaRouteCount);
        }
        Assert.False(lease.IsLost);
        await lease.DisposeAsync();
        Assert.Equal(0, await lease.Completion);
    }

    [Fact]
    public async Task FakePppOwnerNeverTracksPcscfAsRemovableMediaRoute()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var lease = await WindowsHostImsPppBearerLease.StartProtocolTestOwnerAsync(
            FakeOwnerStart(wrongAcknowledgement: false), (_, _) => true);
        var pcscf = IPAddress.Parse("2001:db8::20");
        await lease.EnsureMediaRouteAsync(pcscf);
        Assert.Equal(0, lease.MediaRouteCount);
        await lease.ClearMediaRoutesAsync();
        Assert.False(lease.IsLost);
        await lease.DisposeAsync();
        Assert.Equal(0, await lease.Completion);
    }

    [Fact]
    public async Task FakePppOwnerWrongRouteAcknowledgementFailsClosed()
    {
        if (!OperatingSystem.IsWindows()) return;
        var lease = await WindowsHostImsPppBearerLease.StartProtocolTestOwnerAsync(
            FakeOwnerStart(wrongAcknowledgement: true),
            (_, _) => true);
        try
        {
            await Assert.ThrowsAsync<FormatException>(() =>
                lease.EnsureMediaRouteAsync(IPAddress.Parse("2001:db8::30")));
            Assert.True(lease.IsLost);
            await Assert.ThrowsAsync<InvalidOperationException>(() => lease.RenewLeaseAsync());
            Assert.NotEqual(0, await lease.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            try { await lease.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (InvalidOperationException) when (lease.IsLost) { }
        }
    }

    [Fact]
    public async Task FakePppOwnerCanRejectMediaRouteWithoutDroppingSipBearer()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var lease = await WindowsHostImsPppBearerLease.StartProtocolTestOwnerAsync(
            FakeOwnerStart(wrongAcknowledgement: false, rejectRoute: true),
            (_, _) => true);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lease.EnsureMediaRouteAsync(IPAddress.Parse("2001:db8::30")));
        Assert.False(lease.Completion.IsCompleted);
        await lease.RenewLeaseAsync();
        await lease.DisposeAsync();
        Assert.Equal(0, await lease.Completion);
    }

    [Fact]
    public async Task FakePppOwnerLostMarkerInvalidatesBearerBeforeProcessExits()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var lease = await WindowsHostImsPppBearerLease.StartProtocolTestOwnerAsync(
            FakeOwnerStart(wrongAcknowledgement: false, loseBearerOnRoute: true),
            (_, _) => true);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lease.EnsureMediaRouteAsync(IPAddress.Parse("2001:db8::30")));
        await lease.Lost.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(lease.IsLost);
        Assert.False(lease.Completion.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.RenewLeaseAsync());
        await lease.DisposeAsync();
        Assert.Equal(0, await lease.Completion);
    }

    [Fact]
    public async Task FakePppOwnerExitDuringMediaRouteUnblocksCaller()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var lease = await WindowsHostImsPppBearerLease.StartProtocolTestOwnerAsync(
            FakeOwnerStart(wrongAcknowledgement: false, exitOnRoute: true),
            (_, _) => true);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lease.EnsureMediaRouteAsync(IPAddress.Parse("2001:db8::30")));
        Assert.Equal(4, await lease.Completion);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await lease.DisposeAsync());
    }

    [Fact]
    public async Task PppOwnerStillStopsWhenRegistrationCleanupThrows()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var lease = await WindowsHostImsPppBearerLease.StartProtocolTestOwnerAsync(
            FakeOwnerStart(wrongAcknowledgement: false), (_, _) => true);
        await using var slot = new ModemSlot("test-host-ims-failed-stop", "COM999");
        typeof(ModemSlot).GetField("_hostImsPppLease", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(slot, lease);
        var registrationGate = (SemaphoreSlim)typeof(ModemSlot)
            .GetField("_hostImsRegistrationGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(slot)!;
        registrationGate.Dispose(); // Simulate a SIP cleanup failure before PPP restoration.

        var failure = await Assert.ThrowsAsync<AggregateException>(slot.StopHostImsPppBearerAsync);
        Assert.All(failure.InnerExceptions, error => Assert.IsType<ObjectDisposedException>(error));
        Assert.Equal(0, await lease.Completion);
        Assert.False(slot.GetHostImsPppBearerStatus().IsActive);
        Assert.False(slot.IsHostImsPppTransitionOrActive);
    }

    [Fact]
    public async Task FailedInitialRegistrationStopsItsPppOwnerForUsbVoiceFallback()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var lease = await WindowsHostImsPppBearerLease.StartProtocolTestOwnerAsync(
            FakeOwnerStart(wrongAcknowledgement: false), (_, _) => true);
        await using var slot = new ModemSlot("test-host-ims-initial-fallback", "COM999");
        typeof(ModemSlot).GetField("_hostImsPppLease", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(slot, lease);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => slot.StartHostImsRegistrationAsync());
        Assert.Contains("stable selected SIM", failure.Message);
        Assert.Equal(0, await lease.Completion);
        Assert.False(slot.GetHostImsPppBearerStatus().IsActive);
        Assert.False(slot.IsHostImsPppTransitionOrActive);
        Assert.Equal("failed", slot.GetHostImsRegistrationStatus().State);
        Assert.Contains("PPP stopped", slot.GetHostImsRegistrationStatus().LastError);
    }

    [Fact]
    public async Task StaleInitialRegistrationRollbackCannotStopNewAttempt()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var lease = await WindowsHostImsPppBearerLease.StartProtocolTestOwnerAsync(
            FakeOwnerStart(wrongAcknowledgement: false), (_, _) => true);
        await using var slot = new ModemSlot("test-host-ims-stale-initial", "COM999");
        typeof(ModemSlot).GetField("_hostImsPppLease", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(slot, lease);
        typeof(ModemSlot).GetField("_hostImsRegistrationAttemptGeneration",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(slot, 2L);
        var stop = typeof(ModemSlot).GetMethod("StopHostImsPppBearerCoreAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var staleRollback = (Task<bool>)stop.Invoke(slot, [null, lease, 1L])!;

        Assert.False(await staleRollback);
        Assert.True(slot.GetHostImsPppBearerStatus().IsActive);
        Assert.Equal(0, slot.GetHostImsPppBearerStatus().RenewalCount);
        await lease.RenewLeaseAsync();
        var bearerStatus = slot.GetHostImsPppBearerStatus();
        Assert.Equal(1, bearerStatus.RenewalCount);
        Assert.NotNull(bearerStatus.LastRenewedAtUtc);
        await lease.EnsureMediaRouteAsync(IPAddress.Parse("2001:db8::30"));
        Assert.Equal(1, slot.GetHostImsPppBearerStatus().MediaRouteCount);
        await lease.ClearMediaRoutesAsync();
        Assert.Equal(0, slot.GetHostImsPppBearerStatus().MediaRouteCount);
        await slot.StopHostImsPppBearerAsync();
        Assert.Equal(0, await lease.Completion);
    }

    [Fact]
    public async Task ExpiredHostImsRefreshStopsPppOwnerAndRetainsFailure()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var lease = await WindowsHostImsPppBearerLease.StartProtocolTestOwnerAsync(
            FakeOwnerStart(wrongAcknowledgement: false), (_, _) => true);
        await using var slot = new ModemSlot("test-host-ims-refresh-fallback", "COM999");
        var client = FakeExpiredHostImsClient();
        typeof(ModemSlot).GetField("_hostImsPppLease", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(slot, lease);
        typeof(ModemSlot).GetField("_hostImsRegistration", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(slot, client);

        await slot.RestoreHostImsAfterRefreshFailureAsync(client, "test retry budget exhausted");

        Assert.Equal(0, await lease.Completion);
        Assert.False(slot.GetHostImsPppBearerStatus().IsActive);
        Assert.False(slot.IsHostImsPppTransitionOrActive);
        Assert.Equal("failed", slot.GetHostImsRegistrationStatus().State);
        Assert.Contains("test retry budget exhausted", slot.GetHostImsRegistrationStatus().LastError);
    }

    [Fact]
    public async Task StaleRefreshFailureDoesNotStopReplacementRegistrationOrPpp()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var lease = await WindowsHostImsPppBearerLease.StartProtocolTestOwnerAsync(
            FakeOwnerStart(wrongAcknowledgement: false), (_, _) => true);
        await using var slot = new ModemSlot("test-host-ims-stale-refresh", "COM999");
        await using var stale = FakeExpiredHostImsClient();
        var current = FakeExpiredHostImsClient();
        typeof(ModemSlot).GetField("_hostImsPppLease", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(slot, lease);
        typeof(ModemSlot).GetField("_hostImsRegistration", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(slot, current);

        await slot.RestoreHostImsAfterRefreshFailureAsync(stale, "old session failed");

        Assert.True(slot.GetHostImsPppBearerStatus().IsActive);
        Assert.Same(current, typeof(ModemSlot)
            .GetField("_hostImsRegistration", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(slot));
        await slot.StopHostImsPppBearerAsync();
        Assert.Equal(0, await lease.Completion);
    }

    [Fact]
    public void ReadyFramePreservesInterfaceSourceAndPcscfSet()
    {
        const string frame = "VOWIN_IMS_BEARER_READY {\"InterfaceIndex\":72,\"LocalAddress\":\"240e:54c::1234\",\"PcscfAddresses\":[\"240e:67::1\",\"240e:67::2\"]}";
        var bearer = WindowsHostImsPppBearerLease.ParseReady(frame);
        Assert.Equal(72, bearer.InterfaceIndex);
        Assert.Equal(IPAddress.Parse("240e:54c::1234"), bearer.LocalAddress);
        Assert.Equal([IPAddress.Parse("240e:67::1"), IPAddress.Parse("240e:67::2")],
            bearer.PcscfAddresses);
    }

    [Theory]
    [InlineData("not ready")]
    [InlineData("VOWIN_IMS_BEARER_READY {\"InterfaceIndex\":0,\"LocalAddress\":\"240e:54c::1\",\"PcscfAddresses\":[\"240e:67::1\"]}")]
    [InlineData("VOWIN_IMS_BEARER_READY {\"InterfaceIndex\":72,\"LocalAddress\":\"::1\",\"PcscfAddresses\":[\"240e:67::1\"]}")]
    [InlineData("VOWIN_IMS_BEARER_READY {\"InterfaceIndex\":72,\"LocalAddress\":\"240e:54c::1\",\"PcscfAddresses\":[\"fe80::1\"]}")]
    [InlineData("VOWIN_IMS_BEARER_READY {\"InterfaceIndex\":72,\"LocalAddress\":\"240e:54c::1\",\"PcscfAddresses\":[]}")]
    public void InvalidReadyFramesCannotAuthorizeBearer(string frame) =>
        Assert.ThrowsAny<Exception>(() => WindowsHostImsPppBearerLease.ParseReady(frame));

    [Fact]
    public async Task OptionalRealPppLeaseHasHostRoutableProbeAndRestoresOnDisposal()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_PPP_LEASE") != "1") return;
        var script = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_PPP_OWNER");
        var ras = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_RAS_EXE");
        var port = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_AT_PORT") ?? "COM6";
        Assert.False(string.IsNullOrWhiteSpace(script));
        Assert.False(string.IsNullOrWhiteSpace(ras));

        await using var lease = await WindowsHostImsPppBearerLease.StartAsync(
            script!, ras!, port, maxLeaseSeconds: 180);
        var bearer = Assert.IsType<WindowsHostImsPppBearerInfo>(lease.Bearer);
        await using var modem = new ModemDriver(port);
        modem.Open();
        Assert.True(await modem.PingAsync());
        var probe = await modem.ProbeHostImsPdnAsync();
        Assert.True(probe.CanAttemptWindowsIms, probe.Summary);
        Assert.Contains(probe.EndpointCandidates,
            endpoint => endpoint.LocalAddress.Equals(bearer.LocalAddress) &&
                bearer.PcscfAddresses.Contains(endpoint.PcscfAddress) &&
                probe.IsRouteVerified(endpoint));
        // The modem is disposed before the PPP lease's outer await using scope
        // exits, so the recovery script can reopen COM6 to restore ims=1,1.
    }

    [Fact]
    public async Task OptionalRealPppRenewsPastItsInitialTimeoutWithoutSendingPackets()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_PPP_RENEWAL") != "1") return;
        var script = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_PPP_OWNER");
        var ras = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_RAS_EXE");
        var port = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_AT_PORT") ?? "COM6";
        Assert.True(!string.IsNullOrWhiteSpace(script) && File.Exists(script));
        Assert.True(!string.IsNullOrWhiteSpace(ras) && File.Exists(ras));

        await using var lease = await WindowsHostImsPppBearerLease.StartAsync(
            script!, ras!, port, maxLeaseSeconds: 60);
        Assert.NotNull(lease.Bearer);
        await lease.RenewLeaseAsync();
        Assert.True(lease.RenewalCount >= 1);
        await Task.WhenAny(lease.Completion, Task.Delay(TimeSpan.FromSeconds(75)));
        Assert.True(lease.RenewalCount >= 4);
        Assert.False(lease.Completion.IsCompleted);
        // No SIP REGISTER, AKA, ESP, RTP, SMS or call is attempted.
    }

    [Fact]
    public async Task OptionalRealSlotOwnsAndStopsPppWithoutSmsInitialization()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_PPP_SLOT") != "1") return;
        var script = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_PPP_OWNER");
        var ras = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_RAS_EXE");
        var port = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_AT_PORT") ?? "COM6";
        Assert.False(string.IsNullOrWhiteSpace(script));
        Assert.False(string.IsNullOrWhiteSpace(ras));

        await using var slot = new ModemSlot("real-host-ims-ppp", port);
        Assert.True(await slot.AttachAsync(initializeSms: false));
        Assert.NotNull(slot.Sim);
        var initialImei = slot.Imei;
        var initialIccid = slot.Sim!.Iccid;

        try
        {
            var status = await slot.StartHostImsPppBearerAsync(script!, ras!, maxLeaseSeconds: 180);
            Assert.True(status.IsActive, status.LastError);
            Assert.NotNull(status.Bearer);
            Assert.True(slot.Modem?.IsOpen);
            Assert.False(slot.GetHostImsRegistrationStatus().IsRegistered);
            var probe = await slot.Modem!.ProbeHostImsPdnAsync();
            Assert.True(probe.CanAttemptWindowsIms, probe.Summary);
        }
        finally { await slot.StopHostImsPppBearerAsync(); }

        Assert.False(slot.GetHostImsPppBearerStatus().IsActive);
        Assert.False(slot.IsHostImsPppTransitionOrActive);
        Assert.True(slot.Modem?.IsOpen);
        Assert.True(await slot.Modem!.PingAsync());
        Assert.Equal(initialImei, await slot.Modem.GetImeiAsync());
        Assert.Equal(initialIccid, await slot.Modem.GetIccidAsync());
    }

    [Fact]
    public async Task OptionalRealInvalidRegistrationEndpointRestoresPppBeforeSip()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_PPP_INITIAL_FAILURE") != "1") return;
        var script = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_PPP_OWNER");
        var ras = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_RAS_EXE");
        var port = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_AT_PORT") ?? "COM6";
        Assert.True(!string.IsNullOrWhiteSpace(script) && File.Exists(script));
        Assert.True(!string.IsNullOrWhiteSpace(ras) && File.Exists(ras));

        await using var slot = new ModemSlot("real-host-ims-invalid-registration", port);
        Assert.True(await slot.AttachAsync(initializeSms: false));
        try
        {
            var bearer = await slot.StartHostImsPppBearerAsync(script!, ras!, maxLeaseSeconds: 180);
            Assert.True(bearer.IsActive, bearer.LastError);
            var invalid = new HostImsEndpointCandidate(5, IPAddress.IPv6Loopback, IPAddress.IPv6Loopback);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => slot.StartHostImsRegistrationAsync(selectedEndpoint: invalid));
            Assert.Contains("Select one current IMS", error.Message);
            Assert.False(slot.GetHostImsPppBearerStatus().IsActive);
            Assert.False(slot.IsHostImsPppTransitionOrActive);
            Assert.Contains("PPP stopped", slot.GetHostImsRegistrationStatus().LastError);
            Assert.True(await slot.Modem!.PingAsync());
            // Endpoint validation fails before SIM AKA or the first SIP packet.
        }
        finally { await slot.StopHostImsPppBearerAsync(); }
    }

    [Fact]
    public async Task OptionalRealPppCanReserveAndReleaseWfpOnImsInterfaceWithoutPackets()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_WFP_BEARER") != "1") return;
        var script = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_PPP_OWNER");
        var ras = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_RAS_EXE");
        var port = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_AT_PORT") ?? "COM6";
        Assert.False(string.IsNullOrWhiteSpace(script));
        Assert.False(string.IsNullOrWhiteSpace(ras));

        await using var slot = new ModemSlot("real-host-ims-wfp", port);
        Assert.True(await slot.AttachAsync(initializeSms: false));
        try
        {
            var status = await slot.StartHostImsPppBearerAsync(script!, ras!, maxLeaseSeconds: 180);
            var bearer = Assert.IsType<WindowsHostImsPppBearerInfo>(status.Bearer);
            using var client = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
            using var server = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
            client.Bind(new IPEndPoint(bearer.LocalAddress, 0));
            server.Bind(new IPEndPoint(bearer.LocalAddress, 0));
            var clientPort = ((IPEndPoint)client.LocalEndPoint!).Port;
            var serverPort = ((IPEndPoint)server.LocalEndPoint!).Port;
            Assert.NotEqual(clientPort, serverPort);
            using var backend = new WindowsHostImsSecurityBackend();
            using (var reservation = await backend.ReserveAsync(
                bearer.LocalAddress, bearer.PcscfAddresses[0], bearer.InterfaceIndex,
                clientPort, serverPort))
            {
                Assert.True(reservation.Proposal.SpiClient >= 256);
                Assert.True(reservation.Proposal.SpiServer >= 256);
                Assert.NotEqual(reservation.Proposal.SpiClient, reservation.Proposal.SpiServer);
            }
            backend.RetryPendingCleanup();
            // No SIP REGISTER, AKA, WFP activation, UDP payload or carrier packet is sent.
        }
        finally { await slot.StopHostImsPppBearerAsync(); }
    }

    [Fact]
    public async Task OptionalRealPppAddsAndRemovesExactMediaRouteWithoutPackets()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_PPP_MEDIA_ROUTE") != "1") return;
        var script = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_PPP_OWNER");
        var ras = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_RAS_EXE");
        var port = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_AT_PORT") ?? "COM6";
        Assert.False(string.IsNullOrWhiteSpace(script));
        Assert.False(string.IsNullOrWhiteSpace(ras));

        var media = IPAddress.Parse("2001:db8::1974");
        WindowsHostImsPppBearerInfo bearer;
        await using (var lease = await WindowsHostImsPppBearerLease.StartAsync(
            script!, ras!, port, maxLeaseSeconds: 180))
        {
            bearer = Assert.IsType<WindowsHostImsPppBearerInfo>(lease.Bearer);
            await lease.EnsureMediaRouteAsync(media);
            await lease.EnsureMediaRouteAsync(media); // Idempotent, no duplicate route.
            var selected = Assert.Single(HostImsRouteVerifier.Check(
                [new HostImsEndpointCandidate(5, bearer.LocalAddress, media)]));
            Assert.True(selected.IsVerified, selected.Summary);
            Assert.Equal(bearer.InterfaceIndex, selected.InterfaceIndex);
            // No UDP, RTP, SIP, SMS or call packet is sent to this documentation address.
        }

        var after = Assert.Single(HostImsRouteVerifier.Check(
            [new HostImsEndpointCandidate(5, bearer.LocalAddress, media)]));
        Assert.False(after.IsVerified);
    }

    [Fact]
    public async Task OptionalRealPppReservesCSharpEspRawIoWithoutSendingPackets()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_PPP_USERSPACE_RESERVE") != "1") return;
        var script = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_PPP_OWNER");
        var ras = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_RAS_EXE");
        var port = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_AT_PORT") ?? "COM6";
        var dll = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_WINDIVERT_DLL");
        Assert.True(!string.IsNullOrWhiteSpace(script) && File.Exists(script));
        Assert.True(!string.IsNullOrWhiteSpace(ras) && File.Exists(ras));
        Assert.True(!string.IsNullOrWhiteSpace(dll) && Path.IsPathFullyQualified(dll) &&
            File.Exists(dll) && File.Exists(Path.Combine(Path.GetDirectoryName(dll)!, "WinDivert64.sys")));

        await using var slot = new ModemSlot("real-host-ims-userspace-reserve", port);
        Assert.True(await slot.AttachAsync(initializeSms: false));
        try
        {
            var status = await slot.StartHostImsPppBearerAsync(script!, ras!, maxLeaseSeconds: 180);
            var bearer = Assert.IsType<WindowsHostImsPppBearerInfo>(status.Bearer);
            // Exercise the selected SIM's QMI logical-channel preflight while
            // the real Windows IMS bearer and its watchdog are both active.
            // This verifies the QMI/AT IMEI pairing again but sends no AKA.
            Assert.True(slot.Modem!.IsQmiActive, slot.Modem.LastQmiFallbackReason);
            Assert.True(await slot.Modem.TryCanAuthenticateQmiUsimAsync(),
                slot.Modem.LastQmiFallbackReason);
            var cardIccid = await new Ec25AkaProvider(slot.Modem.Session).ReadIccidFromCardAsync();
            Assert.True(cardIccid is not null && string.Equals(
                Ec25AkaProvider.NormalizeIccid(cardIccid),
                Ec25AkaProvider.NormalizeIccid(slot.Sim!.Iccid), StringComparison.Ordinal),
                "The active card's EF.ICCID changed during IMS PPP handoff.");
            using var media = new VoSharp.Telephony.Calls.RtpSession(null,
                localAddress: bearer.LocalAddress, outgoingInterfaceIndex: bearer.InterfaceIndex);
            Assert.Equal(bearer.LocalAddress, media.LocalAddress);
            Assert.Equal(bearer.InterfaceIndex, media.BoundInterfaceIndex);
            Assert.NotEqual(media.LocalPort, media.LocalRtcpPort);
            using var transport = new VoSharp.Telephony.VoWifi.SipTransport();
            transport.ConnectOnInterface(bearer.LocalAddress, bearer.PcscfAddresses[0],
                bearer.InterfaceIndex, localPort: 0);
            var ports = transport.ReserveProtectedPortsOnInterface();
            var backend = new WindowsHostImsUserspaceSecurityBackend(dll!);
            using var reservation = await backend.ReserveAsync(
                bearer.LocalAddress, bearer.PcscfAddresses[0], bearer.InterfaceIndex,
                ports.ClientPort, ports.ServerPort);
            Assert.Equal(ports.ClientPort, reservation.Proposal.PortClient);
            Assert.Equal(ports.ServerPort, reservation.Proposal.PortServer);
            Assert.NotEqual(reservation.Proposal.SpiClient, reservation.Proposal.SpiServer);
            var raw = Assert.IsAssignableFrom<IHostImsUserspaceSecurityReservation>(reservation);
            Assert.Throws<InvalidOperationException>(() => raw.Send(new VoSharp.Telephony.VoWifi.SipDatagram(
                "not sent"u8.ToArray(), transport.LocalEndPoint!, transport.RemoteEndPoint!)));
            // No REGISTER, AKA, ESP, RTP, SMS or call packet is sent.
        }
        finally { await slot.StopHostImsPppBearerAsync(); }
    }
}
