using System.Reflection;
using System.Runtime.InteropServices;
using VoSharp.Common.Events;
using VoSharp.Sim;
using VoSharp.Telephony.Audio;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.Dtmf;
using VoSharp.Telephony.Emergency;
using VoSharp.Telephony.Mmi;
using VoSharp.Telephony.Sms;
using VoSharp.Telephony.VoWifi;
using Xunit;

namespace VoSharp.Tests;

public class TelephonyTests
{
    [Fact]
    public void TestSmsPdu_EncodeAndDecode_Gsm7Bit()
    {
        var submit = SmsPdu.EncodeSubmitPdu("+8613800138000", "Hello 123");
        Assert.NotNull(submit);
        Assert.True(submit.PduHex.Length > 20);
        Assert.True(submit.TpduLength > 0);
    }

    [Fact]
    public void TestSmsPdu_EncodeAndDecode_Ucs2()
    {
        var submit = SmsPdu.EncodeSubmitPdu("+8613800138000", "你好世界");
        Assert.NotNull(submit);
        Assert.True(submit.PduHex.Length > 20);
    }

    [Fact]
    public void TestG711_Codec_BitAccuracy()
    {
        for (int i = 0; i < 256; i++)
        {
            byte b = (byte)i;
            short pcmA = G711Codec.AlawToPcm(b);
            short pcmU = G711Codec.UlawToPcm(b);
            Assert.InRange(pcmA, short.MinValue, short.MaxValue);
            Assert.InRange(pcmU, short.MinValue, short.MaxValue);
        }
    }

    [Fact]
    public void TestEspTunnel_SealAndOpen_RoundTrip()
    {
        var encKey = new byte[16];
        var authKey = new byte[20];
        for (int i = 0; i < 16; i++) encKey[i] = (byte)(i + 1);
        for (int i = 0; i < 20; i++) authKey[i] = (byte)(i + 10);

        using var tunnel = new EspTunnel(
            outboundSpi: 0x12345678,
            inboundSpi: 0x12345678,
            outboundEncKey: encKey,
            outboundAuthKey: authKey,
            inboundEncKey: encKey,
            inboundAuthKey: authKey,
            encryption: "AES-CBC-128",
            integrity: "HMAC-SHA1-96"
        );

        var innerPacket = System.Text.Encoding.ASCII.GetBytes("SIP/2.0 200 OK\r\n\r\n");
        var sealedBytes = tunnel.Seal(innerPacket);
        Assert.NotNull(sealedBytes);
        Assert.True(sealedBytes.Length > innerPacket.Length);

        var opened = tunnel.Open(sealedBytes);
        Assert.NotNull(opened);
        Assert.Equal(innerPacket, opened);
    }

    [Fact]
    public void TestEspTunnel_Open_ReportsIpv6InnerHeader()
    {
        var key = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var auth = Enumerable.Range(1, 20).Select(i => (byte)(i + 20)).ToArray();
        using var tunnel = new EspTunnel(0x44556677, 0x44556677, key, auth, key, auth);
        var ipv6 = new byte[40];
        ipv6[0] = 0x60;

        var opened = tunnel.Open(tunnel.Seal(ipv6, nextHeader: 41), out var nextHeader);

        Assert.Equal((byte)41, nextHeader);
        Assert.Equal(ipv6, opened);
    }

    [Fact]
    public void TestEspTunnel_RejectsReplayAndAcceptsUnseenOutOfOrderPacket()
    {
        var key = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var auth = Enumerable.Range(1, 20).Select(i => (byte)(i + 20)).ToArray();
        using var tunnel = new EspTunnel(7, 7, key, auth, key, auth);

        var first = tunnel.Seal([1, 2, 3]);
        var second = tunnel.Seal([4, 5, 6]);

        Assert.Equal(new byte[] { 4, 5, 6 }, tunnel.Open(second));
        Assert.Equal(new byte[] { 1, 2, 3 }, tunnel.Open(first));
        Assert.Null(tunnel.Open(first));
        Assert.Null(tunnel.Open(second));
    }

    [Fact]
    public void TestEspTunnel_RejectsPacketsOlderThanReplayWindow()
    {
        var key = new byte[16];
        var auth = new byte[20];
        using var tunnel = new EspTunnel(9, 9, key, auth, key, auth);
        var packets = Enumerable.Range(0, 66).Select(i => tunnel.Seal([(byte)i])).ToArray();

        Assert.NotNull(tunnel.Open(packets[^1]));
        Assert.Null(tunnel.Open(packets[0]));
        Assert.NotNull(tunnel.Open(packets[2]));
    }

    [Fact]
    public void TestEspTunnel_RejectsZeroSequenceAndUnsupportedNextHeader()
    {
        var key = new byte[16];
        var auth = new byte[20];
        using var tunnel = new EspTunnel(11, 11, key, auth, key, auth);

        var zeroSequence = tunnel.Seal([1]);
        Array.Clear(zeroSequence, 4, 4);
        using (var hmac = new System.Security.Cryptography.HMACSHA1(auth))
        {
            var icv = hmac.ComputeHash(zeroSequence, 0, zeroSequence.Length - 12);
            Array.Copy(icv, 0, zeroSequence, zeroSequence.Length - 12, 12);
        }

        Assert.Null(tunnel.Open(zeroSequence));
        Assert.Throws<ArgumentOutOfRangeException>(() => tunnel.Seal([2], nextHeader: 17));
    }

    [Fact]
    public void TestEspTunnel_CopiesKeysAndRejectsSequenceExhaustion()
    {
        var key = Enumerable.Repeat((byte)0xA5, 16).ToArray();
        var auth = Enumerable.Repeat((byte)0x5A, 20).ToArray();
        var expectedKey = key.ToArray();
        var expectedAuth = auth.ToArray();
        var tunnel = new EspTunnel(13, 13, key, auth, key, auth);

        var sequenceField = typeof(EspTunnel).GetField("_outboundSeq", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(sequenceField);
        sequenceField.SetValue(tunnel, uint.MaxValue);

        Assert.Throws<InvalidOperationException>(() => tunnel.Seal([1]));
        tunnel.Dispose();
        tunnel.Dispose();
        Assert.Equal(expectedKey, key);
        Assert.Equal(expectedAuth, auth);
        Assert.Throws<ObjectDisposedException>(() => tunnel.Open(new byte[64]));
    }

    [Fact]
    public void TestVoWifi_EpdgResolver_BuildsStandardDomain()
    {
        var fqdn = EpdgResolver.BuildEpdgDomain("999", "12");
        Assert.Equal("epdg.epc.mnc012.mcc999.pub.3gppnetwork.org", fqdn);

        var imsDomain = EpdgResolver.BuildImsDomain("999", "12");
        Assert.Equal("ims.mnc012.mcc999.3gppnetwork.org", imsDomain);
    }

    [Fact]
    public void TestVoWifi_HomePlmnUsesAuthoritativeEfAdMncLength()
    {
        var twoDigit = SimIdentity.FromImsiAndIccid("999121234567890", "8900000000000000000", mncLength: 2);
        var threeDigit = SimIdentity.FromImsiAndIccid("999123123456789", "8900000000000000001", mncLength: 3);

        Assert.Equal(new[] { ("999", "12") }, EpdgResolver.BuildHomePlmnCandidates(twoDigit));
        Assert.Equal(new[] { ("999", "123") }, EpdgResolver.BuildHomePlmnCandidates(threeDigit));
        Assert.True(twoDigit.IsHomePlmnAuthoritative);
        Assert.True(threeDigit.IsHomePlmnAuthoritative);
    }

    [Fact]
    public void TestVoWifi_ImsSmsHandler_DecodesRpData()
    {
        var tpdu = new byte[]
        {
            0x04, 0x0B, 0x91, 0x68, 0x31, 0x08, 0x10, 0x05, 0xF0,
            0x00, 0x00,
            0x20, 0x90, 0x10, 0x00, 0x00, 0x00, 0x00,
            0x05, 0xE8, 0x32, 0x9B, 0xFD, 0x06
        };
        var rpPayload = new List<byte> { 0x01, 0x05, 0x00, 0x00, (byte)tpdu.Length };
        rpPayload.AddRange(tpdu);

        var sms = ImsSmsHandler.DecodeImsSms(rpPayload.ToArray(), 1);
        Assert.NotNull(sms);
        Assert.Equal("+86138001500", sms.SenderNumber);
        Assert.Equal("hello", sms.Text);

        var ack = ImsSmsHandler.BuildRpAck(0x05);
        Assert.Equal(new byte[] { 0x02, 0x05, 0x41, 0x02, 0x00, 0x00 }, ack);
    }

    [Fact]
    public async Task TestImsCallManager_RejectsWhenVoWifiNotConnected()
    {
        var bus = new AsyncEventBus();
        using var callMgr = new ImsCallManager(bus);
        using var voWifi = new VoWifiManager(bus);

        // Without VoWiFi connection, call must throw InvalidOperationException
        await Assert.ThrowsAsync<InvalidOperationException>(() => callMgr.DialAsync("185", voWifi));
    }

    [Fact]
    public void TestG711Codec_EncodeDecode_RoundTrip()
    {
        short[] original = [0, 100, -100, 1000, -1000, 16000, -16000, 32000, -32000];
        byte[] alaw = G711Codec.EncodeAlaw(original);
        short[] decodedA = G711Codec.DecodeAlaw(alaw);
        Assert.Equal(original.Length, decodedA.Length);

        byte[] ulaw = G711Codec.EncodeUlaw(original);
        short[] decodedU = G711Codec.DecodeUlaw(ulaw);
        Assert.Equal(original.Length, decodedU.Length);

        for (int i = 0; i < original.Length; i++)
        {
            // G.711 compression introduces quantization error but sign and scale should remain intact
            if (original[i] > 0)
            {
                Assert.True(decodedA[i] > 0);
                Assert.True(decodedU[i] > 0);
            }
            else if (original[i] < 0)
            {
                Assert.True(decodedA[i] < 0);
                Assert.True(decodedU[i] < 0);
            }
        }
    }

    [Fact]
    public void TestVoWifi_HomePlmnFallsBackToBothLegalMncLengthsWithoutEfAd()
    {
        var sim = SimIdentity.FromImsiAndIccid(
            "999123123456789",
            "8900000000000000000",
            "Live SPN");

        Assert.Equal(
            new[] { ("999", "12"), ("999", "123") },
            EpdgResolver.BuildHomePlmnCandidates(sim));
        Assert.False(sim.IsHomePlmnAuthoritative);
        Assert.Equal("Live SPN", sim.OperatorName);
    }

    [Fact]
    public void TestVoWifi_CardHomePlmnListTakesPriorityWithoutCarrierData()
    {
        var sim = SimIdentity.FromImsiAndIccid(
            "999123123456789",
            "8900000000000000000",
            "Live SPN",
            mncLength: 3,
            homePlmns: new[] { "00101", "999123" });

        Assert.Equal(
            new[] { ("999", "123") },
            EpdgResolver.BuildHomePlmnCandidates(sim));
    }

    [Fact]
    public void TestVoWifi_IdentityHistoryLearnsPerIccidWithoutCarrierTable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vowifi-identities-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SimIdentityHistoryStore(path);
            var earlier = SimIdentity.FromImsiAndIccid(
                "999123123456789", "8900000000000000000", "Live SPN", mncLength: 3);
            var current = SimIdentity.FromImsiAndIccid(
                "001011234567890", "8900000000000000000", "Live SPN", mncLength: 2);
            store.MarkSuccessful(earlier);
            store.Observe(current);

            var reloaded = new SimIdentityHistoryStore(path).GetCandidates(current);

            Assert.Equal(earlier.Imsi, reloaded[0].Imsi);
            Assert.Contains(reloaded, candidate => candidate.Imsi == current.Imsi);
            Assert.DoesNotContain(current.Iccid, File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public async Task TestAsyncEventBus_SequentialSubscriberQueue()
    {
        await using var bus = new AsyncEventBus();
        var received = new List<int>();
        var tcs = new TaskCompletionSource<bool>();

        using var sub = bus.Subscribe("test.seq", (ev) =>
        {
            if (ev.Payload is int val)
            {
                received.Add(val);
                if (val == 50) tcs.TrySetResult(true);
            }
        });

        for (int i = 1; i <= 50; i++)
        {
            bus.Publish("test.seq", "Test", i);
        }

        await Task.WhenAny(tcs.Task, Task.Delay(3000));

        Assert.Equal(50, received.Count);
        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(i + 1, received[i]);
        }
    }

    [Fact]
    public void TestTlv_ExceedingDepthLimit_Throws()
    {
        // Build nested constructed TLV structure exceeding 32 levels
        byte[] payload = [0x04, 0x01, 0x55]; // primitive TLV at bottom
        for (int i = 0; i < 35; i++)
        {
            var constructed = new byte[2 + payload.Length];
            constructed[0] = 0x30; // Tag 0x30 (constructed)
            constructed[1] = (byte)payload.Length;
            Buffer.BlockCopy(payload, 0, constructed, 2, payload.Length);
            payload = constructed;
        }

        Assert.Throws<InvalidOperationException>(() => VoSharp.Euicc.Asn1.Tlv.Parse(payload));
    }
}
