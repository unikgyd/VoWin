using VoSharp.Kernel;
using VoSharp.Kernel.Pool;
using VoSharp.Sim;

namespace VoSharp.Tests;

public sealed class HostImsRegistrationLifecycleTests
{
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
