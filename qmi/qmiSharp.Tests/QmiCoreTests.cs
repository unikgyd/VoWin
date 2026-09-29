using System.Text;
using qmiSharp.Core;
using qmiSharp.Services;
using Xunit;

namespace qmiSharp.Tests;

public class QmiCoreTests
{
    [Fact]
    public void WdsCurrentSettingsParsesImsApnAndPcscfWithoutConfusingProfileIndexWithCid()
    {
        var response = new QmiPacket(QmiServiceType.WDS, 1, 1, 0x002D,
        [
            new QmiTlv(0x14, "ims"u8.ToArray()),
            new QmiTlv(0x1E, [2, 0, 0, 10]),
            new QmiTlv(0x1F, [0, 7]),
            new QmiTlv(0x23, [1, 1, 0, 0, 10])
        ]);
        var settings = WdsService.ParseCurrentSettings(response);
        Assert.Equal("ims", settings.ApnName);
        Assert.Equal("10.0.0.2", settings.Ipv4Address?.ToString());
        Assert.Equal((byte)7, settings.ProfileIndex);
        Assert.Equal("10.0.0.1", Assert.Single(settings.PcscfServers).ToString());
    }

    [Fact]
    public void WdsCurrentSettingsRejectsTruncatedPcscfList()
    {
        var response = new QmiPacket(QmiServiceType.WDS, 1, 1, 0x002D,
        [new QmiTlv(0x23, [2, 1, 0, 0, 10])]);
        Assert.Throws<FormatException>(() => WdsService.ParseCurrentSettings(response));
    }

    [Fact]
    public void QmuxHeader_Roundtrip_Succeeds()
    {
        var header = new QmuxHeader(0x01, 24, 0x00, (byte)QmiServiceType.DMS, 5);
        Span<byte> buffer = stackalloc byte[QmuxHeader.Size];
        header.WriteTo(buffer);

        var parsed = QmuxHeader.Parse(buffer);
        Assert.Equal(0x01, parsed.IFType);
        Assert.Equal(24, parsed.Length);
        Assert.Equal(0x00, parsed.ControlFlags);
        Assert.Equal((byte)QmiServiceType.DMS, parsed.ServiceType);
        Assert.Equal(5, parsed.ClientId);
    }

    [Fact]
    public void QmuxHeader_InvalidMarker_ThrowsFormatException()
    {
        byte[] buffer = [0x03, 0x00, 0x00, 0x00, 0x00, 0x00];
        bool thrown = false;
        try
        {
            QmuxHeader.Parse(buffer);
        }
        catch (FormatException)
        {
            thrown = true;
        }
        Assert.True(thrown);
    }

    [Fact]
    public void CtlHeader_Roundtrip_Succeeds()
    {
        var header = new CtlHeader(CtlHeader.FlagResponse, 12, 0x0021, 100);
        Span<byte> buffer = stackalloc byte[CtlHeader.Size];
        header.WriteTo(buffer);

        var parsed = CtlHeader.Parse(buffer);
        Assert.True(parsed.IsResponse);
        Assert.False(parsed.IsIndication);
        Assert.Equal(12, parsed.TransactionId);
        Assert.Equal(0x0021, parsed.MessageId);
        Assert.Equal(100, parsed.Length);
    }

    [Fact]
    public void ServiceHeader_Roundtrip_Succeeds()
    {
        var header = new ServiceHeader(ServiceHeader.FlagIndication, 0x1234, 0x002E, 50);
        Span<byte> buffer = stackalloc byte[ServiceHeader.Size];
        header.WriteTo(buffer);

        var parsed = ServiceHeader.Parse(buffer);
        Assert.True(parsed.IsIndication);
        Assert.False(parsed.IsResponse);
        Assert.Equal(0x1234, parsed.TransactionId);
        Assert.Equal(0x002E, parsed.MessageId);
        Assert.Equal(50, parsed.Length);
    }

    [Fact]
    public void Tlv_TypeAccessors_WorkCorrectly()
    {
        var builder = new TlvBuilder()
            .AddByte(0x01, 0xFE)
            .AddSByte(0x02, -75)
            .AddUInt16(0x03, 0xABCD)
            .AddInt16(0x04, -120)
            .AddUInt32(0x05, 0xDEADBEEF)
            .AddInt32(0x06, -999999)
            .AddString(0x07, "Hello QMI!");

        var tlvs = QmiTlv.ParseMultiple(builder.BuildBytes());
        Assert.Equal(7, tlvs.Count);

        Assert.Equal(0xFE, tlvs[0].AsByte());
        Assert.Equal(-75, tlvs[1].AsSByte());
        Assert.Equal(0xABCD, tlvs[2].AsUInt16());
        Assert.Equal(-120, tlvs[3].AsInt16());
        Assert.Equal(0xDEADBEEFU, tlvs[4].AsUInt32());
        Assert.Equal(-999999, tlvs[5].AsInt32());
        Assert.Equal("Hello QMI!", tlvs[6].AsString());
    }

    [Fact]
    public void QmiPacket_MarshalUnmarshal_Roundtrip_Succeeds()
    {
        var builder = new TlvBuilder()
            .AddString(0x01, "TestString")
            .AddUInt32(0x10, 42);

        var original = new QmiPacket(QmiServiceType.NAS, 2, 888, 0x0020, builder.Build());
        byte[] wireBytes = original.Marshal();

        var decoded = QmiPacket.Unmarshal(wireBytes);
        Assert.Equal((ushort)QmiServiceType.NAS, decoded.ServiceType);
        Assert.Equal(2, decoded.ClientId);
        Assert.Equal(888, decoded.TransactionId);
        Assert.Equal(0x0020, decoded.MessageId);
        Assert.Equal(2, decoded.TLVs.Count);
        Assert.Equal("TestString", decoded.GetTlv(0x01)?.AsString());
        Assert.Equal(42U, decoded.GetTlv(0x10)?.AsUInt32());
    }

    [Fact]
    public void QmiPacket_ResultCode_Extraction_Works()
    {
        var packet = new QmiPacket(QmiServiceType.DMS, 1, 1, 0x0025);
        // TLV 0x02: result = Success (0), error = None (0)
        packet.TLVs.Add(new QmiTlv(0x02, new byte[] { 0x00, 0x00, 0x00, 0x00 }));

        bool ok = packet.TryGetResult(out var result, out var error);
        Assert.True(ok);
        Assert.Equal(QmiResultCode.Success, result);
        Assert.Equal(QmiErrorCode.None, error);

        // CheckResult should not throw
        packet.CheckResult();

        // Failure packet
        var failPacket = new QmiPacket(QmiServiceType.DMS, 1, 1, 0x0025);
        failPacket.TLVs.Add(new QmiTlv(0x02, new byte[] { 0x01, 0x00, 0x05, 0x00 })); // Result=1(Failure), Error=5(DeviceNotReady)

        Assert.True(failPacket.TryGetResult(out var fRes, out var fErr));
        Assert.Equal(QmiResultCode.Failure, fRes);
        Assert.Equal(QmiErrorCode.DeviceNotReady, fErr);

        Assert.Throws<QmiException>(() => failPacket.CheckResult());
    }

    [Fact]
    public void TryFindFrame_Resynchronizes_NoisyBuffer()
    {
        byte[] noisy = [
            0xFF, 0xFE, 0x00, // Junk noise
            0x01, 0x0B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x27, 0x00, 0x00, 0x00, // Valid 12-byte CTL packet
            0xAA, 0xBB // Trailing noise
        ];

        bool found = QmiPacket.TryFindFrame(noisy, out int start, out int length);
        Assert.True(found);
        Assert.Equal(3, start);
        Assert.Equal(12, length);

        var packet = QmiPacket.Unmarshal(noisy.AsSpan(start, length));
        Assert.Equal((ushort)QmiServiceType.Control, packet.ServiceType);
        Assert.Equal(0x0027, packet.MessageId);
    }
}
