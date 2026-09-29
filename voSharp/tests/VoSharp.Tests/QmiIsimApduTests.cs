using System.Threading.Channels;
using qmiSharp.Client;
using qmiSharp.Core;
using qmiSharp.Services;
using qmiSharp.Transport;
using VoSharp.Common.Aka;
using VoSharp.Modem;

namespace VoSharp.Tests;

public sealed class QmiIsimApduTests
{
    [Fact]
    public async Task QmiUsimPreflightAndAkaUseSelectedGwApplication()
    {
        await using var wire = new ScriptedIsimTransport();
        await using var reader = new QmiModemReader(() => wire);

        Assert.True(await reader.CanAuthenticateUsimAsync());
        var result = await reader.AuthenticateUsimAsync(
            AkaChallenge.Create(new byte[16], new byte[16]));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, wire.AkaCount);
        Assert.Equal(2, wire.OpenCount);
        Assert.Equal(2, wire.CloseCount);
        Assert.All(wire.OpenedAids, aid => Assert.Equal("A0000000871002", aid));
        result.Clear();
    }

    [Fact]
    public async Task MultiplePhysicalSlotsDoNotGuessAnApduTarget()
    {
        await using var wire = new ScriptedIsimTransport { ReportSecondSlot = true };
        await using var client = await QmiClient.CreateAsync(wire,
            new QmiClientOptions { SyncOnOpen = false });
        await using var uim = new UimService(client);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            QmiIsimApduTransport.CreateAsync(uim, CancellationToken.None));
        Assert.Equal(0, wire.OpenCount);
    }

    [Fact]
    public async Task QmiLogicalChannelReadsIsimIdentityAndRunsAka()
    {
        await using var wire = new ScriptedIsimTransport();
        await using var client = await QmiClient.CreateAsync(wire,
            new QmiClientOptions { SyncOnOpen = false });
        await using var uim = new UimService(client);
        var transport = await QmiIsimApduTransport.CreateAsync(uim, CancellationToken.None);
        var isim = new IsimIdentityReader(transport);

        var identity = await isim.ReadAsync();
        Assert.Equal(IsimIdentityReadiness.Available, identity.Readiness);
        Assert.Equal("001010123456789@ims.mnc001.mcc001.3gppnetwork.org",
            identity.Identity!.PrivateIdentity);
        Assert.Equal("sip:+15551234567@ims.mnc001.mcc001.3gppnetwork.org",
            identity.Identity.DefaultPublicIdentity);

        var aka = await isim.AuthenticateAsync(AkaChallenge.Create(new byte[16], new byte[16]));
        Assert.True(aka.Success, aka.ErrorMessage);
        Assert.Equal(8, aka.Res!.Length);
        Assert.Equal(2, wire.OpenCount);
        Assert.Equal(2, wire.CloseCount);
        Assert.True(wire.AkaCount == 1);
        Assert.All(wire.ApduChannels, channel => Assert.Equal((byte)4, channel));
        aka.Clear();
    }

    private sealed class ScriptedIsimTransport : IQmiTransport
    {
        private readonly Channel<byte[]> _responses = Channel.CreateUnbounded<byte[]>();
        private byte _selectedFile;
        public int OpenCount { get; private set; }
        public int CloseCount { get; private set; }
        public int AkaCount { get; private set; }
        public bool ReportSecondSlot { get; init; }
        public List<string> OpenedAids { get; } = [];
        public List<byte> ApduChannels { get; } = [];
        public string Name => "scripted-isim";
        public bool IsConnected => true;

        public ValueTask SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var request = QmiPacket.Unmarshal(buffer.Span);
            var tlvs = new List<QmiTlv> { new(0x02, [0, 0, 0, 0]) };
            if (request.ServiceType == (ushort)QmiServiceType.Control && request.MessageId == 0x0022)
                tlvs.Add(new QmiTlv(0x01, [(byte)QmiServiceType.UIM, 1]));
            switch (request.MessageId)
            {
                case 0x002F:
                    tlvs.Add(new QmiTlv(0x10, CardStatus()));
                    break;
                case 0x0047:
                    tlvs.Add(new QmiTlv(0x10, ReportSecondSlot
                        ? [2, 2, 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0]
                        : [1, 2, 0, 0, 0, 1, 0, 0, 0, 1, 0]));
                    break;
                case 0x0042:
                    Assert.Equal((byte)1, request.GetTlv(0x01)!.AsByte());
                    var aidWithLength = request.GetTlv(0x10)!.Value.Span;
                    Assert.Equal((byte)7, aidWithLength[0]);
                    var aid = Convert.ToHexString(aidWithLength[1..]);
                    Assert.Contains(aid, new[] { "A0000000871002", "A0000000871004" });
                    OpenedAids.Add(aid);
                    OpenCount++;
                    tlvs.Add(new QmiTlv(0x10, [4]));
                    tlvs.Add(new QmiTlv(0x11, [0x90, 0]));
                    break;
                case 0x003B:
                    Assert.Equal((byte)1, request.GetTlv(0x01)!.AsByte());
                    ApduChannels.Add(request.GetTlv(0x10)!.AsByte());
                    var apduWithLength = request.GetTlv(0x02)!.Value.Span;
                    var apdu = apduWithLength[2..];
                    Assert.Equal((byte)0x40, apdu[0]);
                    var cardReply = RespondToApdu(apdu);
                    tlvs.Add(new QmiTlv(0x10, [(byte)cardReply.Length, 0, .. cardReply]));
                    break;
                case 0x003F:
                    Assert.Equal((byte)4, request.GetTlv(0x11)!.AsByte());
                    CloseCount++;
                    break;
            }
            var response = new QmiPacket(request.ServiceType, request.ClientId,
                request.TransactionId, request.MessageId, tlvs) { IsResponse = true };
            _responses.Writer.TryWrite(response.Marshal());
            return ValueTask.CompletedTask;
        }

        private byte[] RespondToApdu(ReadOnlySpan<byte> apdu)
        {
            if (apdu[1] == 0xA4)
            {
                _selectedFile = apdu[^1];
                return [0x90, 0];
            }
            if (apdu[1] is 0xB0 or 0xB2)
            {
                var value = _selectedFile switch
                {
                    2 => "001010123456789@ims.mnc001.mcc001.3gppnetwork.org",
                    3 => "ims.mnc001.mcc001.3gppnetwork.org",
                    4 => "sip:+15551234567@ims.mnc001.mcc001.3gppnetwork.org",
                    _ => throw new InvalidOperationException("Wrong ISIM EF selected")
                };
                var bytes = System.Text.Encoding.UTF8.GetBytes(value);
                if (apdu[^1] == 0) return [0x6C, checked((byte)(bytes.Length + 2))];
                return [0x80, checked((byte)bytes.Length), .. bytes, 0x90, 0];
            }
            if (apdu[1] == 0x88)
            {
                AkaCount++;
                return [0xDB, 0x2B, 0x08, .. Enumerable.Repeat((byte)0x11, 8),
                    0x10, .. Enumerable.Repeat((byte)0x22, 16),
                    0x10, .. Enumerable.Repeat((byte)0x33, 16), 0x90, 0];
            }
            throw new InvalidOperationException($"Unexpected APDU INS {apdu[1]:X2}");
        }

        private static byte[] CardStatus()
        {
            var value = new byte[9 + 6 + 21 + 21];
            value[8] = 1;
            value[9] = 1;
            value[14] = 2;
            value[15] = 2;
            value[16] = 7;
            value[21] = 7;
            Convert.FromHexString("A0000000871002").CopyTo(value.AsSpan(22, 7));
            value[36] = 5;
            value[37] = 7;
            value[42] = 7;
            Convert.FromHexString("A0000000871004").CopyTo(value.AsSpan(43, 7));
            return value;
        }

        public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var bytes = await _responses.Reader.ReadAsync(cancellationToken);
            bytes.CopyTo(buffer);
            return bytes.Length;
        }

        public ValueTask DisposeAsync()
        {
            _responses.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public void Dispose() => _responses.Writer.TryComplete();
    }
}
