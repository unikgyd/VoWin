using VoSharp.Kernel;

namespace VoSharp.Tests;

public class TelephonyRoutePolicyTests
{
    [Theory]
    [InlineData(TelephonyMode.ModemBridge)]
    [InlineData(TelephonyMode.HostIms)]
    public void WifiRemainsIndependentWhenCellularIsStopped(TelephonyMode owner)
    {
        Assert.Equal(TelephonyMode.VoWifi, TelephonyRoutePolicy.Resolve(TelephonyMode.VoWifi,
            false, true, true, true, cellularImsMode: owner));
        Assert.Throws<InvalidOperationException>(() => TelephonyRoutePolicy.Resolve(TelephonyMode.VoWifi,
            false, true, true, true, forceCellular: true, cellularImsMode: owner));
    }

    [Theory]
    [InlineData(TelephonyMode.ModemBridge)]
    [InlineData(TelephonyMode.HostIms)]
    public void CellularOverrideAlwaysUsesSelectedImsOwner(TelephonyMode owner)
    {
        Assert.Equal(owner, TelephonyRoutePolicy.Resolve(TelephonyMode.VoWifi, true, true, true, true,
            forceCellular: true, cellularImsMode: owner));
        Assert.Equal(owner, TelephonyRoutePolicy.Resolve(TelephonyMode.ModemBridge, true, true, true, true,
            cellularImsMode: owner));
        Assert.Equal(TelephonyMode.VoWifi, TelephonyRoutePolicy.Resolve(TelephonyMode.VoWifi,
            true, true, true, true, cellularImsMode: owner));
    }

    [Fact]
    public void HostOwnerNeverFallsBackToModemOrWifiWhenRegistrationFails()
    {
        Assert.Throws<InvalidOperationException>(() => TelephonyRoutePolicy.Resolve(TelephonyMode.ModemBridge,
            true, true, false, true, cellularImsMode: TelephonyMode.HostIms));
        Assert.Throws<ArgumentOutOfRangeException>(() => TelephonyRoutePolicy.Resolve(TelephonyMode.VoWifi,
            true, true, true, true, cellularImsMode: TelephonyMode.VoWifi));
    }

    [Theory]
    [InlineData(TelephonyMode.ModemBridge)]
    [InlineData(TelephonyMode.HostIms)]
    [InlineData(TelephonyMode.VoWifi)]
    public void SelectedModeWinsEvenWhenAllTransportsAreReady(TelephonyMode mode) =>
        Assert.Equal(mode, TelephonyRoutePolicy.Resolve(mode, true, true, true, true));

    [Theory]
    [InlineData(TelephonyMode.ModemBridge, true, true, false)]
    [InlineData(TelephonyMode.HostIms, true, false, true)]
    [InlineData(TelephonyMode.VoWifi, false, true, true)]
    public void SelectedModeNeverSilentlyFallsBack(TelephonyMode mode, bool wifi, bool host, bool modem) =>
        Assert.Throws<InvalidOperationException>(() => TelephonyRoutePolicy.Resolve(mode, true, wifi, host, modem));

    [Theory]
    [InlineData(TelephonyMode.ModemBridge)]
    [InlineData(TelephonyMode.HostIms)]
    [InlineData(TelephonyMode.VoWifi)]
    public void StoppedLineDoesNotSendOnAnyTransport(TelephonyMode mode) =>
        Assert.Throws<InvalidOperationException>(() => TelephonyRoutePolicy.Resolve(mode, false, true, true, true, forceCellular: true));

    [Theory]
    [InlineData(true, true, true, TelephonyMode.VoWifi)]
    [InlineData(false, true, true, TelephonyMode.HostIms)]
    [InlineData(false, false, true, TelephonyMode.ModemBridge)]
    public void LibraryClientsWithoutASelectionKeepAutomaticRouting(bool wifi, bool host, bool modem, TelephonyMode expected) =>
        Assert.Equal(expected, TelephonyRoutePolicy.Resolve(null, true, wifi, host, modem));

    [Fact]
    public void ExplicitDiagnosticOverrideMustAlsoBeReady()
    {
        Assert.Equal(TelephonyMode.ModemBridge,
            TelephonyRoutePolicy.Resolve(TelephonyMode.VoWifi, true, true, false, true, forceCellular: true));
        Assert.Throws<InvalidOperationException>(() =>
            TelephonyRoutePolicy.Resolve(TelephonyMode.ModemBridge, true, false, false, true, forceVoWifi: true));
        Assert.Throws<ArgumentException>(() =>
            TelephonyRoutePolicy.Resolve(null, true, true, true, true, true, true));
    }
}
