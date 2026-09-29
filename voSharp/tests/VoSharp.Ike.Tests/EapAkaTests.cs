using System.Buffers.Binary;
using System.Security.Authentication;
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
        var prime = EapAkaClient.BuildPermanentIdentity("460001234567890", "460", "00", EapType.AkaPrime);
        Assert.StartsWith("6460001234567890@", Encoding.UTF8.GetString(prime));
    }

    [Fact]
    public void DeriveAkaPrimeKeys_MatchesRfc5448AppendixCCase1()
    {
        var keys = EapAkaClient.DeriveAkaPrimeKeys(
            Encoding.ASCII.GetBytes("0555444333222111"),
            Convert.FromHexString("9744871ad32bf9bbd1dd5ce54e3e2e5a"),
            Convert.FromHexString("5349fbe098649f948f5d2e973a81c00f"),
            Convert.FromHexString("bb52e91c747ac3ab2a5c23d15ee351d5"),
            Encoding.ASCII.GetBytes("WLAN"));

        Assert.Equal("766FA0A6C317174B812D52FBCD11A179", Convert.ToHexString(keys.KEncr));
        Assert.Equal("0842EA722FF6835BFA2032499FC3EC23C2F0E388B4F07543FFC677F1696D71EA", Convert.ToHexString(keys.KAut));
        Assert.Equal("67C42D9AA56C1B79E295E3459FC3D187D42BE0BF818D3070E362C5E967A4D544E8ECFE19358AB3039AFF03B7C930588C055BABEE58A02650B067EC4E9347C75A", Convert.ToHexString(keys.Msk));
        Assert.Equal("F861703CD775590E16C7679EA3874ADA866311DE290764D760CF76DF647EA01C313F69924BDD7650CA9BAC141EA075C4EF9E8029C0E290CDBAD5638B63BC23FB", Convert.ToHexString(keys.Emsk));
    }

    [Fact]
    public async Task AkaPrimeSelection_RequiresFreshIdentity()
    {
        var provider = new MockAkaProvider();
        var akaClient = new EapAkaClient(provider, "460001234567890", "460", "00");
        var primeRequest = EapAkaClient.MarshalEapPacket(new EapPacket(
            EapCode.Request, 1, EapType.AkaPrime, [AkaSubtype.Identity, 0, 0]));
        var mismatch = await Assert.ThrowsAsync<EapMethodMismatchException>(() => akaClient.HandleAsync(primeRequest));
        Assert.Equal(EapType.AkaPrime, mismatch.RequestedType);

        var primeClient = new EapAkaClient(provider, "460001234567890", "460", "00",
            selectedEapMethod: EapType.AkaPrime);
        Assert.Equal((byte)'6', primeClient.Identity[0]);
        var identityRequest = EapAkaClient.MarshalEapPacket(new EapPacket(
            EapCode.Request, 1, EapType.Identity, []));
        var (response, complete) = await primeClient.HandleAsync(identityRequest);
        Assert.False(complete);
        Assert.Equal(primeClient.Identity, EapAkaClient.ParseEapPacket(response!).Data);
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

        var failure = EapAkaClient.MarshalEapPacket(new EapPacket(EapCode.Failure, 3, 0, Array.Empty<byte>()));
        var exception = await Assert.ThrowsAsync<AuthenticationException>(() => client.HandleAsync(failure));
        Assert.Contains("local failure", exception.Message, StringComparison.Ordinal);
        Assert.Contains("USIM authentication command", exception.Message, StringComparison.Ordinal);
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
