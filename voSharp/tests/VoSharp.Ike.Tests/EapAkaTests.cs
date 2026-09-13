using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using VoSharp.Common.Aka;
using Xunit;

namespace VoSharp.Ike.Tests;

public class EapAkaTests
{
    private sealed class MockAkaProvider : IAkaProvider
    {
        public byte[] Res { get; init; } = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 };
        public byte[] Ck { get; init; } = new byte[16];
        public byte[] Ik { get; init; } = new byte[16];

        public MockAkaProvider()
        {
            Array.Fill(Ck, (byte)0xCC);
            Array.Fill(Ik, (byte)0xEE);
        }

        public Task<bool> CheckReadyAsync(string? expectedIccid = null, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<AkaResult> AuthenticateAsync(AkaChallenge challenge, CancellationToken ct = default) =>
            Task.FromResult(AkaResult.Succeeded(Res, Ck, Ik));
    }

    [Fact]
    public void BuildPermanentIdentity_FormatsCorrectly()
    {
        var naiBytes = EapAkaClient.BuildPermanentIdentity("460001234567890", "460", "00");
        var nai = Encoding.UTF8.GetString(naiBytes);

        Assert.Equal("0460001234567890@nai.epc.mnc000.mcc460.3gppnetwork.org", nai);

        var nai3Digit = EapAkaClient.BuildPermanentIdentity("460011234567890", "460", "011");
        Assert.Equal("0460011234567890@nai.epc.mnc011.mcc460.3gppnetwork.org", Encoding.UTF8.GetString(nai3Digit));
    }

    [Fact]
    public void EapPacket_MarshalAndParse_RoundTrips()
    {
        var original = new EapPacket(
            Code: EapCode.Request,
            Identifier: 42,
            Type: EapType.Identity,
            Data: Encoding.UTF8.GetBytes("test-identity")
        );

        var encoded = EapAkaClient.MarshalEapPacket(original);
        var parsed = EapAkaClient.ParseEapPacket(encoded);

        Assert.Equal(original.Code, parsed.Code);
        Assert.Equal(original.Identifier, parsed.Identifier);
        Assert.Equal(original.Type, parsed.Type);
        Assert.True(original.Data.SequenceEqual(parsed.Data));
    }

    [Fact]
    public void AkaAttributes_MarshalAndParse_RoundTrips()
    {
        var rand = new byte[16];
        Array.Fill(rand, (byte)0xAA);
        var randValue = new byte[18]; // 2 reserved + 16 rand
        Buffer.BlockCopy(rand, 0, randValue, 2, 16);
        var attr = EapAkaClient.MarshalAkaAttribute(AkaAttributeType.Rand, randValue);

        var parsed = EapAkaClient.ParseAkaAttributes(attr);
        Assert.Single(parsed);
        Assert.Equal(AkaAttributeType.Rand, parsed[0].Type);
        Assert.Equal(20, parsed[0].Raw.Length);
        Assert.True(rand.SequenceEqual(parsed[0].Raw[4..20]));
    }

    [Fact]
    public void DeriveAkaKeys_GeneratesValidKeyLengths()
    {
        var identity = Encoding.UTF8.GetBytes("0460001234567890@nai.epc.mnc000.mcc460.3gppnetwork.org");
        var ik = new byte[16];
        var ck = new byte[16];
        Array.Fill(ik, (byte)0x11);
        Array.Fill(ck, (byte)0x22);

        var keys = EapAkaClient.DeriveAkaKeys(identity, ik, ck);

        Assert.Equal(16, keys.KEncr.Length);
        Assert.Equal(16, keys.KAut.Length);
        Assert.Equal(64, keys.Msk.Length);
        Assert.Equal(64, keys.Emsk.Length);

        // K_encr and K_aut must be distinct
        Assert.False(keys.KEncr.SequenceEqual(keys.KAut));
    }

    [Fact]
    public async Task EapAkaClient_HandlesChallengeFlow_Success()
    {
        var mockSim = new MockAkaProvider();
        var client = new EapAkaClient(mockSim, "460001234567890", "460", "00");

        // 1. Server sends EAP-Request/Identity
        var idReq = EapAkaClient.MarshalEapPacket(new EapPacket(EapCode.Request, 1, EapType.Identity, Array.Empty<byte>()));
        var (idRespBytes, idDone) = await client.HandleAsync(idReq);

        Assert.False(idDone);
        Assert.NotNull(idRespBytes);
        var idResp = EapAkaClient.ParseEapPacket(idRespBytes!);
        Assert.Equal(EapCode.Response, idResp.Code);
        Assert.Equal(1, idResp.Identifier);
        Assert.Equal(EapType.Identity, idResp.Type);
        Assert.True(client.Identity.SequenceEqual(idResp.Data));

        // 2. Compute expected keys for challenge
        var expectedKeys = EapAkaClient.DeriveAkaKeys(client.Identity, mockSim.Ik, mockSim.Ck);

        // Build server challenge with AT_RAND, AT_AUTN, AT_MAC
        var rand = new byte[16];
        Array.Fill(rand, (byte)0x12);
        var autn = new byte[16];
        Array.Fill(autn, (byte)0x34);

        var randAttr = EapAkaClient.MarshalAkaAttribute(AkaAttributeType.Rand, Combine(new byte[2], rand));
        var autnAttr = EapAkaClient.MarshalAkaAttribute(AkaAttributeType.Autn, Combine(new byte[2], autn));
        var emptyMac = EapAkaClient.MarshalAkaAttribute(AkaAttributeType.Mac, new byte[18]);

        var chalData = Combine(new byte[] { AkaSubtype.Challenge, 0, 0 }, randAttr, autnAttr, emptyMac);
        var chalReq = new EapPacket(EapCode.Request, 2, EapType.Aka, chalData);
        var chalReqBytes = EapAkaClient.MarshalEapPacket(chalReq);

        // Sign server MAC using K_aut
        var mac = HMACSHA1.HashData(expectedKeys.KAut, chalReqBytes)[..16];
        // Mac in chalReqBytes is located at 4 header + 1 type + 3 subtype + randAttr(20) + autnAttr(20) + 4 = 52
        Buffer.BlockCopy(mac, 0, chalReqBytes, 52, 16);

        // 3. Client handles challenge
        var (chalRespBytes, chalDone) = await client.HandleAsync(chalReqBytes);
        Assert.False(chalDone);
        Assert.NotNull(chalRespBytes);
        Assert.True(client.ChallengeComplete);
        Assert.NotNull(client.Keys);
        Assert.Equal(64, client.Keys!.Msk.Length);

        var chalResp = EapAkaClient.ParseEapPacket(chalRespBytes!);
        Assert.Equal(EapCode.Response, chalResp.Code);
        Assert.Equal(2, chalResp.Identifier);
        Assert.Equal(EapType.Aka, chalResp.Type);
        Assert.Equal(AkaSubtype.Challenge, chalResp.Data[0]);

        var respAttrs = EapAkaClient.ParseAkaAttributes(chalResp.Data.AsSpan(3));
        var resAttr = respAttrs.First(a => a.Type == AkaAttributeType.Res);
        var resBitLength = BinaryPrimitives.ReadUInt16BigEndian(resAttr.Raw.AsSpan(2, 2));
        Assert.Equal(mockSim.Res.Length * 8, resBitLength);
        Assert.True(mockSim.Res.SequenceEqual(resAttr.Raw[4..(4 + mockSim.Res.Length)]));

        // 4. Server sends EAP-Success
        var successPacket = new byte[] { EapCode.Success, 2, 0, 4 };
        var (successResp, isSuccess) = await client.HandleAsync(successPacket);
        Assert.True(isSuccess);
        Assert.Null(successResp);
    }

    [Fact]
    public async Task EapAkaClient_ReportsFailedUsimResultWithoutReadingUnavailableKeys()
    {
        var diagnostics = new List<string>();
        var failedProvider = new FailedAkaProvider();
        var client = new EapAkaClient(
            failedProvider, "460001234567890", "460", "00", diagnosticLog: diagnostics.Add);

        var randAttr = EapAkaClient.MarshalAkaAttribute(AkaAttributeType.Rand, new byte[18]);
        var autnAttr = EapAkaClient.MarshalAkaAttribute(AkaAttributeType.Autn, new byte[18]);
        var macAttr = EapAkaClient.MarshalAkaAttribute(AkaAttributeType.Mac, new byte[18]);
        var challenge = EapAkaClient.MarshalEapPacket(new EapPacket(
            EapCode.Request, 3, EapType.Aka,
            Combine(new byte[] { AkaSubtype.Challenge, 0, 0 }, randAttr, autnAttr, macAttr)));

        var (response, isSuccess) = await client.HandleAsync(challenge);

        Assert.False(isSuccess);
        Assert.NotNull(response);
        var parsed = EapAkaClient.ParseEapPacket(response!);
        Assert.Equal(AkaSubtype.AuthenticationReject, parsed.Data[0]);
        Assert.Contains(diagnostics, line => line.Contains("success=False", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, line => line.Contains("RES is unavailable", StringComparison.Ordinal));
    }

    private sealed class FailedAkaProvider : IAkaProvider
    {
        public Task<bool> CheckReadyAsync(string? expectedIccid = null, CancellationToken ct = default) => Task.FromResult(true);

        public Task<AkaResult> AuthenticateAsync(AkaChallenge challenge, CancellationToken ct = default) =>
            Task.FromResult(AkaResult.Failed("USIM authentication command returned a non-success status."));
    }

    private static byte[] Combine(params byte[][] arrays)
    {
        var total = arrays.Sum(a => a.Length);
        var res = new byte[total];
        var offset = 0;
        foreach (var arr in arrays)
        {
            Buffer.BlockCopy(arr, 0, res, offset, arr.Length);
            offset += arr.Length;
        }
        return res;
    }
}
