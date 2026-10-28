using System.Net;
using VoSharp.Kernel;
using VoSharp.Kernel.Pool;
using VoSharp.Modem;
using VoSharp.Sim;
using VoSharp.Sip;
using System.Reflection;

namespace VoSharp.Tests;

public sealed class HostImsRegistrationLifecycleTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task IdempotentStopPreservesThePreviousRemoteDeregistrationResult(int confirmation)
    {
        await using var slot = new ModemSlot("host-idempotent-stop", "COM999");
        typeof(ModemSlot).GetField("_hostImsRemoteDeregistration", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(slot, confirmation);
        await slot.StopHostImsRegistrationAsync();
        await slot.StopHostImsPppBearerAsync();
        await slot.StopHostImsRegistrationAsync();
        Assert.Equal(confirmation == 1, slot.GetHostImsRegistrationStatus().RemoteDeregistered);
    }
    [Fact]
    public async Task AutomaticAttemptAbortDoesNotEmitManualStopIntent()
    {
        await using var slot = new ModemSlot("host-stop-intent", "COM999");
        var stops = 0;
        slot.HostImsManualStopRequested += () => stops++;
        await slot.AbortHostImsRecoveryAttemptAsync();
        Assert.Equal(0, stops);
        Assert.Equal(HostImsLossKind.None, slot.GetHostImsRegistrationStatus().LossKind);
        await slot.StopHostImsRegistrationAsync();
        Assert.Equal(1, stops);
        Assert.Equal(HostImsLossKind.ManuallyStopped, slot.GetHostImsRegistrationStatus().LossKind);
        await slot.StopHostImsPppBearerAsync();
        Assert.Equal(2, stops);
    }
    [Fact]
    public async Task HostRegistrarTimerSurvivesStopAndExplicitRetryWithoutTouchingHardware()
    {
        var historyPath = Path.Combine(Path.GetTempPath(), $"host-ims-backoff-{Guid.NewGuid():N}.json");
        try
        {
            await using var slot = new ModemSlot("test-host-backoff", "COM999", identityHistory: new SimIdentityHistoryStore(historyPath));
            var sim = SimIdentity.FromImsiAndIccid("001011234567890", "8900000000000000000", mncLength: 2);
            slot.SetVerifiedSimIdentity(sim);
            var gate = (SipRegistrationRetryGate)typeof(ModemSlot).GetField("_hostImsRetryGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(slot)!;
            gate.Observe(sim.Iccid, new(503, 300, false, true, DateTime.UtcNow));
            Assert.NotNull(slot.GetHostImsRegistrationStatus().RetryNotBeforeUtc);
            Assert.Equal(503, slot.GetHostImsRegistrationStatus().Failure!.StatusCode);
            Assert.Throws<SipRegistrationRetryBlockedException>(slot.PrepareExplicitHostImsRetry);
            await slot.StopHostImsRegistrationAsync();
            await slot.StopHostImsPppBearerAsync();
            Assert.Throws<SipRegistrationRetryBlockedException>(slot.PrepareExplicitHostImsRetry);
            Assert.Null(slot.Modem);
            slot.SetVerifiedSimIdentity(SimIdentity.FromImsiAndIccid("001011234567891", "8900000000000000001", mncLength: 2));
            slot.PrepareExplicitHostImsRetry();
            Assert.Null(slot.GetHostImsRegistrationStatus().Failure);
            Assert.Null(slot.GetHostImsRegistrationStatus().RetryNotBeforeUtc);
        }
        finally { if (File.Exists(historyPath)) File.Delete(historyPath); }
    }

    [Fact]
    public void ManagedPppAutomaticallySelectsFirstAdvertisedVerifiedPcscf()
    {
        var local = IPAddress.Parse("2001:db8::10");
        var first = IPAddress.Parse("2001:db8::20");
        var second = IPAddress.Parse("2001:db8::21");
        var unrelated = new HostImsEndpointCandidate(4, local, first);
        var secondCandidate = new HostImsEndpointCandidate(5, local, second);
        var firstCandidate = new HostImsEndpointCandidate(5, local, first);
        var candidates = new[] { unrelated, secondCandidate, firstCandidate };
        var bearer = new WindowsHostImsPppBearerInfo(9, local, [first, second]);
        var routes = candidates.Select(candidate =>
            new HostImsRouteCheck(candidate, true, "fake owned IMS", 9, "Verified")).ToArray();

        Assert.Equal(firstCandidate, ModemSlot.SelectHostImsEndpoint(candidates, null, bearer, routes));
        Assert.Equal(secondCandidate, ModemSlot.SelectHostImsEndpoint(candidates, secondCandidate, bearer, routes));
        Assert.Null(ModemSlot.SelectHostImsEndpoint([secondCandidate], null,
            bearer with { PcscfAddresses = [first] }, routes));
        Assert.Null(ModemSlot.SelectHostImsEndpoint(candidates, null, null));
        Assert.Equal(firstCandidate, ModemSlot.SelectHostImsEndpoint([firstCandidate], null, null));
    }

    [Fact]
    public void CompletedPppOwnerCannotAuthorizeHostImsStopSignaling()
    {
        var owner = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(ModemSlot.IsPppOwnerAvailableForSignaling(null)); // WWAN bearer has no PPP owner.
        Assert.True(ModemSlot.IsPppOwnerAvailableForSignaling(owner.Task));
        owner.SetResult(4);
        Assert.False(ModemSlot.IsPppOwnerAvailableForSignaling(owner.Task));
    }

    [Fact]
    public async Task SlotWithoutSimCannotStartAndCanClearFailureState()
    {
        await using var slot = new ModemSlot("test-host-ims", "COM999");
        Assert.Equal("stopped", slot.GetHostImsRegistrationStatus().State);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            slot.StartHostImsRegistrationAsync());
        Assert.Contains("stable selected SIM", error.Message);
        Assert.Equal("failed", slot.GetHostImsRegistrationStatus().State);

        await slot.StopHostImsRegistrationAsync();
        Assert.Equal("stopped", slot.GetHostImsRegistrationStatus().State);
        Assert.Null(slot.GetHostImsRegistrationStatus().RemoteDeregistered);
    }

    [Fact]
    public async Task PppBearerRequiresVerifiedPhysicalSlotAndStopWithoutLeaseIsSafe()
    {
        await using var slot = new ModemSlot("test-host-ims-ppp", "COM999");
        Assert.False(slot.GetHostImsPppBearerStatus().IsActive);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            slot.StartHostImsPppBearerAsync("missing.ps1", "missing.exe"));
        Assert.Contains("verified SIM", error.Message);
        Assert.False(slot.GetHostImsPppBearerStatus().IsActive);
        Assert.NotNull(slot.GetHostImsPppBearerStatus().LastError);

        await slot.StopHostImsPppBearerAsync();
        Assert.False(slot.IsHostImsPppTransitionOrActive);
    }

    [Fact]
    public async Task KernelRegistrationCommandDoesNotSendAnythingWithoutSelectedModem()
    {
        await using var kernel = new VoKernel();
        var status = await kernel.ExecuteCommandAsync("host-ims status");
        var register = await kernel.ExecuteCommandAsync("host-ims register");
        var malformed = await kernel.ExecuteCommandAsync("host-ims register --cid 2 --local 10.0.0.2");

        Assert.False(status.Success);
        Assert.False(register.Success);
        Assert.False(malformed.Success);
        Assert.Contains("Usage", malformed.Message);
    }

    [Fact]
    public async Task ConfirmedCardAbsenceClearsOldIdentityAndReportsAbsence()
    {
        var historyPath = Path.Combine(Path.GetTempPath(), $"host-ims-sim-history-{Guid.NewGuid():N}.json");
        try
        {
            await using var slot = new ModemSlot("test-card-removal", "COM999",
                identityHistory: new SimIdentityHistoryStore(historyPath));
            var states = new List<string>();
            slot.SimChanged += (_, args) => states.Add(args.State);
            var identity = SimIdentity.FromImsiAndIccid(
                "001011234567890", "8900000000000000000", mncLength: 2,
                reportedImsi: "001011234567890", permanentImsi: "001011234567890",
                imsiSource: "USIM EF.IMSI", hasPlmnConflict: true);
            slot.SetVerifiedSimIdentity(identity);

            Assert.Equal(identity.Iccid, slot.Sim?.Iccid);
            Assert.Equal("001", slot.StableRoutingMcc);
            Assert.True(slot.HasImsiPlmnConflict);
            slot.ClearConfirmedAbsentSimIdentity();

            Assert.Null(slot.Sim);
            Assert.Null(slot.LastReportedImsi);
            Assert.Null(slot.LastPermanentImsi);
            Assert.Null(slot.StableRoutingMcc);
            Assert.Equal("unknown", slot.ImsiIdentitySource);
            Assert.False(slot.HasImsiPlmnConflict);
            Assert.Equal("ABSENT", states[^1]);
        }
        finally
        {
            if (File.Exists(historyPath)) File.Delete(historyPath);
        }
    }
}
