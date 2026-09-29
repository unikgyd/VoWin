using qmiSharp.Core;
using qmiSharp.Services;
using Xunit;

namespace qmiSharp.Tests;

public sealed class NasParsingTests
{
    [Fact]
    public void SignalStrengthKeepsSignedDbmAndRadioInterface()
    {
        var response = new QmiPacket(QmiServiceType.NAS, 1, 1, 0x0020,
        [
            new QmiTlv(0x01, [unchecked((byte)-85), 0x08]),
            new QmiTlv(0x16, [unchecked((byte)-10), 0x08]),
            new QmiTlv(0x18, [0x9C, 0xFF])
        ]);
        var signal = NasService.ParseSignalStrength(response);
        Assert.Equal(-85, signal.Rssi);
        Assert.Equal(-10, signal.Rsrq);
        Assert.Equal(-100, signal.Rsrp);
        Assert.Equal(0x08, signal.RadioInterface);
    }

    [Fact]
    public void ServingSystemUsesCurrentPlmnNotDataCapabilityTlv()
    {
        var response = new QmiPacket(QmiServiceType.NAS, 1, 1, 0x0024,
        [
            new QmiTlv(0x01, [1, 1, 1, 0, 1, 0x08]),
            new QmiTlv(0x10, [0]),
            new QmiTlv(0x11, [0xFF, 0xFF, 0xFF, 0xFF]),
            // libqmi's Get Serving System fixture: MCC 222, MNC 50, GSM-7 "Iliad".
            new QmiTlv(0x12, [0xDE, 0x00, 0x32, 0x00, 5, 0x49, 0x76, 0x3A, 0x4C, 0x06])
        ]);
        var serving = NasService.ParseServingSystem(response);
        Assert.Equal(NasRegistrationState.Registered, serving.RegistrationState);
        Assert.True(serving.CsAttached);
        Assert.True(serving.PsAttached);
        Assert.True(serving.Roaming);
        Assert.Equal((ushort)222, serving.Mcc);
        Assert.Equal((ushort)50, serving.Mnc);
        Assert.Equal("Iliad", serving.PlmnName);
        Assert.Equal(0x08, serving.RadioInterface);
    }

    [Fact]
    public void MissingMandatoryNasDataDoesNotLookLikeNoSignalOrRegistration()
    {
        Assert.Throws<FormatException>(() => NasService.ParseSignalStrength(
            new QmiPacket(QmiServiceType.NAS, 1, 1, 0x0020)));
        Assert.Throws<FormatException>(() => NasService.ParseServingSystem(
            new QmiPacket(QmiServiceType.NAS, 1, 1, 0x0024,
                [new QmiTlv(0x01, [1, 1, 1, 0, 1])])));
    }
}
