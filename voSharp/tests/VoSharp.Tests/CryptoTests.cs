using VoSharp.Common.Utils;
using VoSharp.Crypto;
using VoSharp.Sim;
using Xunit;

namespace VoSharp.Tests;

public class CryptoTests
{
    [Fact]
    public void TestMilenage_3GPP_TS35208_TestSet1()
    {
        // 3GPP TS 35.208 Section 4.1 Test Set 1
        var k = HexUtils.FromHexString("465b5ce8b199b49faa5f0a2ee238a6bc");
        var rand = HexUtils.FromHexString("23553cbe9637a89d218ae64dae47bf35");
        var sqn = HexUtils.FromHexString("ff9bb4d0b607");
        var amf = HexUtils.FromHexString("b9b9");
        var opc = HexUtils.FromHexString("cd63cb71954a9f4e48a5994e37a02baf");

        var expectedMacA = HexUtils.FromHexString("4a9ffac354dfafb3");
        var expectedRes = HexUtils.FromHexString("a54211d5e3ba50bf");
        var expectedCk = HexUtils.FromHexString("b40ba9a3c58b2a05bbf0d987b21bf8cb");
        var expectedIk = HexUtils.FromHexString("f769bcd751044604127672711c6d3441");
        var expectedAk = HexUtils.FromHexString("aa689c648370");

        var aka = Milenage.GenerateAkaVectors(opc, k, rand, sqn, amf);

        Assert.Equal(expectedMacA, aka.MacA);
        Assert.Equal(expectedRes, aka.Res);
        Assert.Equal(expectedCk, aka.Ck);
        Assert.Equal(expectedIk, aka.Ik);
        Assert.Equal(expectedAk, aka.Ak);

        // Verify AUTN = (SQN ^ AK) || AMF || MAC-A
        // SQN (ff9bb4d0b607) ^ AK (aa689c648370) = 55f328b43577
        var expectedAutn = HexUtils.FromHexString("55f328b43577b9b94a9ffac354dfafb3");
        Assert.Equal(expectedAutn, aka.Autn);
    }

    [Fact]
    public void TestMilenage_3GPP_TS35208_TestSet2_OPcComputation()
    {
        // 3GPP TS 35.208 Section 4.2 Test Set 2
        var k = HexUtils.FromHexString("465b5ce8b199b49faa5f0a2ee238a6bc");
        var op = HexUtils.FromHexString("cdc202d5123e20f62b6d676ac72cb318");
        var expectedOpc = HexUtils.FromHexString("cd63cb71954a9f4e48a5994e37a02baf");

        var computedOpc = Milenage.ComputeOPc(k, op);
        Assert.Equal(expectedOpc, computedOpc);
    }

    [Fact]
    public void TestDigestAka_ResponseComputation()
    {
        var username = "460001234567890@ims.mnc000.mcc460.3gppnetwork.org";
        var realm = "ims.mnc000.mcc460.3gppnetwork.org";
        var res = HexUtils.FromHexString("a54211d5e3ba50bf");
        var nonce = "23553cbe9637a89d218ae64dae47bf35";
        var method = "REGISTER";
        var uri = "sip:ims.mnc000.mcc460.3gppnetwork.org";
        var nc = "00000001";
        var cnonce = "0a4f113b";
        var qop = "auth";

        var response = DigestAka.ComputeResponse(username, realm, res, method, uri, nonce, nc, cnonce, qop);
        Assert.NotEmpty(response);
        Assert.Equal(32, response.Length); // 32 hex chars MD5
    }

    [Fact]
    public void TestHardwareAka_BuildAndParseApdu()
    {
        var rand = HexUtils.FromHexString("23553cbe9637a89d218ae64dae47bf35");
        var autn = HexUtils.FromHexString("55f328b43577b9b94a9ffac354dfafb3");

        var apdu = HardwareAka.BuildAuthenticateApdu(rand, autn);
        Assert.Equal(39, apdu.Length);
        Assert.Equal(0x00, apdu[0]);
        Assert.Equal(0x88, apdu[1]);

        // Mock DB 3GPP Success Response (RES=8 bytes, CK=16 bytes, IK=16 bytes, SW1=90, SW2=00)
        // DB [Len=43] [ResLen=8] [RES:8] [CkLen=16] [CK:16] [IkLen=16] [IK:16] 90 00
        var mockRes = HexUtils.FromHexString("a54211d5e3ba50bf");
        var mockCk = HexUtils.FromHexString("b40ba9a3c58b2a05bbf0d987b21bf8cb");
        var mockIk = HexUtils.FromHexString("f769bcd751044604127672711c6d3441");

        var respList = new List<byte> { 0xDB, 43, 8 };
        respList.AddRange(mockRes);
        respList.Add(16);
        respList.AddRange(mockCk);
        respList.Add(16);
        respList.AddRange(mockIk);
        respList.Add(0x90);
        respList.Add(0x00);

        var parseResult = HardwareAka.ParseAuthenticateResponse(respList.ToArray());
        Assert.True(parseResult.Success);
        Assert.False(parseResult.SyncFailure);
        Assert.Equal(mockRes, parseResult.Res);
        Assert.Equal(mockCk, parseResult.Ck);
        Assert.Equal(mockIk, parseResult.Ik);
    }
}
