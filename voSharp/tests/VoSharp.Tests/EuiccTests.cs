using VoSharp.Common.Events;
using VoSharp.Common.Utils;
using VoSharp.Euicc;
using VoSharp.Euicc.Asn1;
using VoSharp.Euicc.Models;
using VoSharp.Euicc.Sgp22;
using VoSharp.Euicc.Transport;
using VoSharp.Kernel;
using Xunit;

namespace VoSharp.Tests;

public class MockEuiccTransport : IEuiccTransport
{
    public string BackendName => "Mock eUICC";
    public List<byte[]> TransmittedApdus { get; } = [];
    public Func<byte[], byte[]>? ApduHandler { get; set; }

    public Task<int> OpenLogicalChannelAsync(string aid, CancellationToken ct = default)
    {
        return Task.FromResult(1);
    }

    public Task<byte[]> TransmitLogicalChannelAsync(int channel, byte[] apdu, CancellationToken ct = default)
    {
        TransmittedApdus.Add(apdu);
        if (ApduHandler != null)
        {
            return Task.FromResult(ApduHandler(apdu));
        }

        // Default: return SW 90 00
        return Task.FromResult(new byte[] { 0x90, 0x00 });
    }

    public Task CloseLogicalChannelAsync(int channel, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}

public class EuiccTests
{
    [Theory]
    [InlineData("LPA:1$smdp.example.com$MATCH-123", "smdp.example.com", "MATCH-123")]
    [InlineData("1$smdp.example.com$MATCH-123", "smdp.example.com", "MATCH-123")]
    [InlineData("https://carrier.example/install?activationCode=LPA%3A1%24smdp.example.com%24MATCH-123", "smdp.example.com", "MATCH-123")]
    public void ActivationCode_NormalizesSupportedInputs(string input, string address, string matchingId)
    {
        var parsed = EuiccActivationCode.Parse(input);

        Assert.Equal("LPA:1$smdp.example.com$MATCH-123", parsed.CanonicalCode);
        Assert.Equal(address, parsed.SmdpAddress);
        Assert.Equal(matchingId, parsed.MatchingId);
    }

    [Fact]
    public void ActivationCode_PreservesConfirmationCodeRequirement()
    {
        var parsed = EuiccActivationCode.Parse("LPA:1$smdp.example.com$MATCH-123$$1");

        Assert.True(parsed.ConfirmationCodeRequired);
        Assert.Equal("LPA:1$smdp.example.com$MATCH-123$$1", parsed.CanonicalCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://smdp.example.com/profile")]
    [InlineData("LPA:2$smdp.example.com$MATCH")]
    [InlineData("LPA:1$https://evil.example/path$MATCH")]
    [InlineData("LPA:1$user@smdp.example.com$MATCH")]
    [InlineData("LPA:1$smdp.example.com/path$MATCH")]
    [InlineData("LPA:1$smdp.example.com$MATCH$$2")]
    public void ActivationCode_RejectsMalformedOrAmbiguousInputs(string input)
    {
        Assert.Throws<FormatException>(() => EuiccActivationCode.Parse(input));
    }

    [Fact]
    public async Task DownloadProfile_RequiresConfirmationCodeBeforeOpeningCard()
    {
        var transport = new MockEuiccTransport();
        var manager = new EuiccManager(transport);

        await Assert.ThrowsAsync<ArgumentException>(() => manager.DownloadProfileAsync(
            "LPA:1$smdp.example.com$MATCH$$1",
            "490154203237518"));
        Assert.Empty(transport.TransmittedApdus);
    }

    [Fact]
    public void TestTlv_PrimitiveAndConstructed_ParseAndEncode()
    {
        // 1. Primitive TLV
        var prim = new Tlv(0x5A, new byte[] { 0x89, 0x86, 0x04 });
        var encodedPrim = prim.Encode();
        Assert.Equal("5A03898604", HexUtils.ToHexString(encodedPrim));

        var (parsedPrim, consumed1) = Tlv.Parse(encodedPrim);
        Assert.Equal((uint)0x5A, parsedPrim.Tag);
        Assert.Equal(encodedPrim.Length, consumed1);
        Assert.Equal("898604", HexUtils.ToHexString(parsedPrim.Value));

        // 2. Multi-byte Tag & Constructed TLV (BF2D)
        var root = new Tlv(0xBF2D);
        var child1 = new Tlv(0xE3);
        child1.Children.Add(new Tlv(0x5A, Tlv.IccidToBcd("89860412345678901234")));
        child1.Children.Add(new Tlv(0x9F70, new byte[] { 0x01 })); // Enabled
        child1.Children.Add(new Tlv(0x90, "China Mobile eSIM"));
        root.Children.Add(child1);

        var encodedRoot = root.Encode();
        var (parsedRoot, consumedRoot) = Tlv.Parse(encodedRoot);
        Assert.Equal((uint)0xBF2D, parsedRoot.Tag);
        Assert.Single(parsedRoot.Children);

        var parsedE3 = parsedRoot.FindFirstChild(0xE3);
        Assert.NotNull(parsedE3);
        Assert.Equal(3, parsedE3.Children.Count);

        var iccidTlv = parsedE3.FindFirstChild(0x5A);
        Assert.NotNull(iccidTlv);
        Assert.Equal("89860412345678901234", Tlv.BcdToIccid(iccidTlv.Value));

        var nickTlv = parsedE3.FindFirstChild(0x90);
        Assert.NotNull(nickTlv);
        Assert.Equal("China Mobile eSIM", nickTlv.TextValue());
    }

    [Fact]
    public async Task TestSgp22_GetProfilesInfo_Parsing()
    {
        var mock = new MockEuiccTransport();

        // Build mock GetProfilesInfo response (BF2D containing 2 profiles)
        var respTlv = new Tlv(0xBF2D);

        // Profile 1: Active
        var p1 = new Tlv(0xE3);
        p1.Children.Add(new Tlv(0x5A, Tlv.IccidToBcd("89860123456789012345")));
        p1.Children.Add(new Tlv(0x4F, HexUtils.FromHexString("A0000005591010FFFFFFFF8900001100")));
        p1.Children.Add(new Tlv(0x9F70, new byte[] { 0x01 })); // Enabled
        p1.Children.Add(new Tlv(0x90, "Personal eSIM"));
        p1.Children.Add(new Tlv(0x91, "China Telecom"));
        p1.Children.Add(new Tlv(0x92, "5G Ultra"));
        respTlv.Children.Add(p1);

        // Profile 2: Disabled
        var p2 = new Tlv(0xE3);
        p2.Children.Add(new Tlv(0x5A, Tlv.IccidToBcd("89860223456789012345")));
        p2.Children.Add(new Tlv(0x4F, HexUtils.FromHexString("A0000005591010FFFFFFFF8900002200")));
        p2.Children.Add(new Tlv(0x9F70, new byte[] { 0x00 })); // Disabled
        p2.Children.Add(new Tlv(0x90, "Travel Roaming"));
        p2.Children.Add(new Tlv(0x91, "Vodafone UK"));
        respTlv.Children.Add(p2);

        var payload = respTlv.Encode();
        var fullResp = new byte[payload.Length + 2];
        payload.CopyTo(fullResp, 0);
        fullResp[^2] = 0x90;
        fullResp[^1] = 0x00;

        mock.ApduHandler = apdu => fullResp;

        var client = new Sgp22Client(mock, 1);
        var profiles = await client.GetProfilesInfoAsync();

        Assert.Equal(2, profiles.Count);

        // Check Profile 1
        Assert.Equal("89860123456789012345", profiles[0].ICCID);
        Assert.Equal("A0000005591010FFFFFFFF8900001100", profiles[0].ISDPAID);
        Assert.Equal(ProfileState.Enabled, profiles[0].State);
        Assert.Equal("Personal eSIM", profiles[0].Nickname);
        Assert.Equal("China Telecom", profiles[0].ServiceProviderName);

        // Check Profile 2
        Assert.Equal("89860223456789012345", profiles[1].ICCID);
        Assert.Equal(ProfileState.Disabled, profiles[1].State);
        Assert.Equal("Travel Roaming", profiles[1].Nickname);
        Assert.Equal("Vodafone UK", profiles[1].ServiceProviderName);
    }

    [Fact]
    public async Task TestSgp22_EnableProfile_CommandGeneration()
    {
        var mock = new MockEuiccTransport();

        // Return success for EnableProfile (BF31 with result 0x00)
        var respTlv = new Tlv(0xBF31);
        respTlv.Children.Add(new Tlv(0x80, new byte[] { 0x00 })); // ResultCode = 0 (success)
        var payload = respTlv.Encode();
        var fullResp = new byte[payload.Length + 2];
        payload.CopyTo(fullResp, 0);
        fullResp[^2] = 0x90;
        fullResp[^1] = 0x00;

        mock.ApduHandler = apdu => fullResp;

        var client = new Sgp22Client(mock, 1);
        await client.EnableProfileAsync("89860123456789012345", refresh: true);

        Assert.Single(mock.TransmittedApdus);
        var sentApdu = mock.TransmittedApdus[0];

        // Header: CLA(81) INS(E2) P1(91) P2(00) Lc(...)
        Assert.Equal(0x81, sentApdu[0]);
        Assert.Equal(0xE2, sentApdu[1]);

        // Parse sent TLV
        var (sentTlv, _) = Tlv.Parse(sentApdu.AsSpan(5));
        Assert.Equal((uint)0xBF31, sentTlv.Tag);

        // Child 1: A0 (profileIdentifier CHOICE) containing ICCID (5A)
        var a0Tlv = sentTlv.FindFirstChild(0xA0);
        Assert.NotNull(a0Tlv);
        var iccidTlv = a0Tlv.FindFirstChild(0x5A);
        Assert.NotNull(iccidTlv);
        Assert.Equal("89860123456789012345", Tlv.BcdToIccid(iccidTlv.Value));

        // Child 2: refreshFlag (81) == FF
        var refreshTlv = sentTlv.FindFirstChild(0x81);
        Assert.NotNull(refreshTlv);
        Assert.Equal(0xFF, refreshTlv.Value[0]);
    }

    [Fact]
    public async Task TestEuiccManager_Lifecycle_And_Events()
    {
        var eventBus = new AsyncEventBus();
        var mock = new MockEuiccTransport();

        // 1. GetProfilesInfo mock
        var respTlv = new Tlv(0xBF2D);
        var p = new Tlv(0xE3);
        p.Children.Add(new Tlv(0x5A, Tlv.IccidToBcd("89860411223344556677")));
        p.Children.Add(new Tlv(0x9F70, new byte[] { 0x01 }));
        p.Children.Add(new Tlv(0x90, "Primary SIM"));
        respTlv.Children.Add(p);

        var payload = respTlv.Encode();
        var fullResp = new byte[payload.Length + 2];
        payload.CopyTo(fullResp, 0);
        fullResp[^2] = 0x90;
        fullResp[^1] = 0x00;

        mock.ApduHandler = apdu => fullResp;

        var manager = new EuiccManager(mock, eventBus);

        bool eventFired = false;
        eventBus.Subscribe("euicc.profiles.listed", ev =>
        {
            eventFired = true;
        });

        var list = await manager.ListProfilesAsync();
        Assert.Single(list);
        Assert.Equal("89860411223344556677", list[0].ICCID);

        await Task.Delay(50);
        Assert.True(eventFired);
    }

    [Fact]
    public async Task TestKernel_EuiccCommands()
    {
        await using var kernel = new VoKernel();
        var mock = new MockEuiccTransport();

        // Setup mock response for GetProfilesInfo
        var respTlv = new Tlv(0xBF2D);
        var p1 = new Tlv(0xE3);
        p1.Children.Add(new Tlv(0x5A, Tlv.IccidToBcd("89860123456789012345")));
        p1.Children.Add(new Tlv(0x9F70, new byte[] { 0x01 })); // Enabled
        p1.Children.Add(new Tlv(0x90, "Work eSIM"));
        respTlv.Children.Add(p1);

        var p2 = new Tlv(0xE3);
        p2.Children.Add(new Tlv(0x5A, Tlv.IccidToBcd("89860223456789012345")));
        p2.Children.Add(new Tlv(0x9F70, new byte[] { 0x00 })); // Disabled
        p2.Children.Add(new Tlv(0x90, "Personal eSIM"));
        respTlv.Children.Add(p2);

        var payload = respTlv.Encode();
        var fullResp = new byte[payload.Length + 2];
        payload.CopyTo(fullResp, 0);
        fullResp[^2] = 0x90;
        fullResp[^1] = 0x00;

        mock.ApduHandler = apdu => fullResp;

        kernel.EuiccManager = new EuiccManager(mock, kernel.EventBus);

        // 1. List profiles
        var listRes = await kernel.ExecuteCommandAsync("euicc list");
        Assert.True(listRes.Success);
        Assert.Contains("2", listRes.Message);

        // 2. Switch profile
        // Return success for EnableProfile
        var enableRespTlv = new Tlv(0xBF31);
        enableRespTlv.Children.Add(new Tlv(0x80, new byte[] { 0x00 }));
        var enablePayload = enableRespTlv.Encode();
        var enableFullResp = new byte[enablePayload.Length + 2];
        enablePayload.CopyTo(enableFullResp, 0);
        enableFullResp[^2] = 0x90;
        enableFullResp[^1] = 0x00;

        mock.ApduHandler = apdu => enableFullResp;

        var switchRes = await kernel.ExecuteCommandAsync("euicc switch 89860223456789012345");
        Assert.True(switchRes.Success);
        Assert.Contains("Switched and activated", switchRes.Message);

        // 3. Rename profile
        var renameRespTlv = new Tlv(0xBF29);
        renameRespTlv.Children.Add(new Tlv(0x80, new byte[] { 0x00 }));
        var renPayload = renameRespTlv.Encode();
        var renFullResp = new byte[renPayload.Length + 2];
        renPayload.CopyTo(renFullResp, 0);
        renFullResp[^2] = 0x90;
        renFullResp[^1] = 0x00;

        mock.ApduHandler = apdu => renFullResp;

        var renameRes = await kernel.ExecuteCommandAsync("euicc rename 89860223456789012345 HolidaySIM");
        Assert.True(renameRes.Success);
        Assert.Contains("HolidaySIM", renameRes.Message);

        // 4. Backend switch command
        var backendRes = await kernel.ExecuteCommandAsync("euicc backend pcsc");
        Assert.True(backendRes.Success);

        // 5. Ports list command
        var portsRes = await kernel.ExecuteCommandAsync("ports");
        Assert.True(portsRes.Success);
    }
}
