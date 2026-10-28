using qmiSharp.Transport;
using VoSharp.Kernel.Pool;
using VoSharp.Sim;
using VoSharp.Kernel;

namespace VoSharp.Tests;

public sealed class ModemDiscoveryPolicyTests
{
    [Fact]
    public async Task MissingExplicitSlotCannotStopOrProbeTheOtherSelectedModem()
    {
        await using var kernel = new VoKernel();
        var selected = new ModemSlot("selected", "COM999");
        Assert.True(kernel.Pool.TryAddSlot(selected));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => kernel.StartVoWifiAsync("removed"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => kernel.StopVoWifiAsync("removed"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => kernel.ProbeVoWifiLivenessAsync("removed"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => kernel.RefreshSignalAsync("removed"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => kernel.RefreshRegistrationAsync("removed"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => kernel.RefreshSimAsync("removed"));
        Assert.Null(kernel.GetVoWifiDiagnostics("removed"));
        Assert.Same(selected, kernel.Pool.ActiveSlot);
        Assert.False(selected.Modem?.IsOpen == true);
    }

    private static readonly Guid China = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Philippines = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static WindowsUsbFunction Function(string port, Guid container, string kind = "AT") =>
        new($"USB\\test-{port}", container, Guid.Empty, $"Quectel USB {kind} Port ({port})", port, null, null);

    [Fact]
    public void HostImsOnOneModemStillAllowsTheOtherPhysicalModemToBeDiscovered()
    {
        var inventory = new[] { Function("COM6", China), Function("COM9", China, "Modem"),
            Function("COM10", Philippines), Function("COM11", Philippines, "NMEA") };
        Assert.Equal(["COM10"], ModemDiscoveryPolicy.SelectPorts(["COM6", "COM9", "COM10", "COM11", "COM3"],
            inventory, [("COM6", China)]));
    }

    [Fact]
    public void RestartingOwnerRetainsItsContainerEvenWhileItsAtPortIsAbsent()
    {
        var inventory = new[] { Function("COM9", China, "Modem"), Function("COM10", Philippines) };
        Assert.Equal(["COM10"], ModemDiscoveryPolicy.SelectPorts(["COM9", "COM10"], inventory, [("COM6", China)]));
    }

    [Fact]
    public void MissingOwnerIdentityDoesNotAuthorizeProbingItsPossibleSiblings()
    {
        Assert.Empty(ModemDiscoveryPolicy.SelectPorts(["COM10"], [Function("COM10", Philippines)], [("COM6", null)]));
    }

    [Fact]
    public void InitialUsbScanUsesTheAtFunctionInsteadOfOpeningEverySibling()
    {
        var inventory = new[] { Function("COM10", Philippines), Function("COM11", Philippines, "NMEA"),
            Function("COM12", Philippines, "DM") };
        Assert.Equal(["COM10", "COM42"], ModemDiscoveryPolicy.SelectPorts(["COM10", "COM11", "COM12", "COM42"], inventory, []));
    }

    [Fact]
    public async Task SharedOrMissingSimIdentityCannotMergeTwoPhysicalModems()
    {
        await using var pool = new ModemPool();
        var first = new ModemSlot("a", "COM96");
        var second = new ModemSlot("b", "COM97");
        var shared = SimIdentity.FromImsiAndIccid("515660000000001", "shared-test-card");
        foreach (var slot in new[] { first, second })
        {
            slot.SetVerifiedSimIdentity(shared);
            Assert.True(pool.TryAddSlot(slot));
        }
        Assert.Equal(2, (await pool.DeduplicateDiscoveredSlotsAsync([first, second])).Count);
        Assert.Equal(2, pool.Slots.Count);
    }
}
