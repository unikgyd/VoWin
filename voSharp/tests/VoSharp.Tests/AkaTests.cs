using VoSharp.Common.Aka;
using VoSharp.Modem;
using VoSharp.Modem.At;
using VoSharp.Sim;

namespace VoSharp.Tests;

/// <summary>
/// Offline verification of the USIM AKA path (gate G2). Nothing here touches hardware, the
/// network, strongSwan or WFP, so it runs anywhere.
/// </summary>
public class AkaTests
{
    private static readonly byte[] Rand = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
    private static readonly byte[] Autn = Convert.FromHexString("101112131415161718191a1b1c1d1e1f");

    private static readonly byte[] Res = Convert.FromHexString("d1e2a3c4b5a69788");
    private static readonly byte[] Ck = Convert.FromHexString("2b7e151628aed2a6abf7158809cf4f3c");
    private static readonly byte[] Ik = Convert.FromHexString("a0b1c2d3e4f5061728394a5b6c7d8e9f");

    /// <summary>Encoding A: <c>DB | totalLen | RES(len) | CK(len) | IK(len) | SW</c>.</summary>
    private static byte[] BuildSuccessResponse() =>
        Tagged("DB", Tlv(Res) + Tlv(Ck) + Tlv(Ik));

    /// <summary>Encoding B: no outer length byte, as produced by some stacks.</summary>
    private static byte[] BuildSuccessResponseWithoutOuterLength() =>
        FromHex("DB" + Tlv(Res) + Tlv(Ck) + Tlv(Ik));

    private static byte[] BuildSyncFailureResponse(byte[] auts) =>
        Tagged("DC", Tlv(auts));

    // ── APDU construction ────────────────────────────────────────────────────

    [Fact]
    public void BuildAuthenticateProducesTs31102Apdu()
    {
        var apdu = HardwareAka.BuildAuthenticateApdu(Rand, Autn);

        // 00 88 00 81 22 | 10 <RAND 16B> 10 <AUTN 16B>
        Assert.Equal("0088008122" +
                     "10" + Convert.ToHexString(Rand) +
                     "10" + Convert.ToHexString(Autn),
            Convert.ToHexString(apdu));
    }

    [Fact]
    public void ChallengeRejectsWrongSizes()
    {
        Assert.Throws<ArgumentException>(() => AkaChallenge.Create(Rand, new byte[15]));
        Assert.Throws<ArgumentException>(() => AkaChallenge.Create(new byte[8], Autn));
    }

    // ── Response parsing ─────────────────────────────────────────────────────

    [Fact]
    public void ParseSuccessResponseExtractsResCkIk()
    {
        var result = HardwareAka.ParseAuthenticateResponse(BuildSuccessResponse());

        Assert.True(result.Success);
        Assert.Equal(Convert.ToHexString(Res), Convert.ToHexString(result.Res!));
        Assert.Equal(Convert.ToHexString(Ck), Convert.ToHexString(result.Ck!));
        Assert.Equal(Convert.ToHexString(Ik), Convert.ToHexString(result.Ik!));
    }

    [Fact]
    public void ParseAcceptsResponsesWithoutAnOuterLengthByte()
    {
        // CK/IK being fixed at 16 bytes is what lets the parser tell the two encodings apart.
        var result = HardwareAka.ParseAuthenticateResponse(BuildSuccessResponseWithoutOuterLength());

        Assert.True(result.Success);
        Assert.Equal(Convert.ToHexString(Res), Convert.ToHexString(result.Res!));
        Assert.Equal(Convert.ToHexString(Ck), Convert.ToHexString(result.Ck!));
        Assert.Equal(Convert.ToHexString(Ik), Convert.ToHexString(result.Ik!));
    }

    [Fact]
    public void ParseSyncFailureResponseReturnsFourteenByteAuts()
    {
        var auts = Convert.FromHexString("000102030405060708090a0b0c0d");
        var result = HardwareAka.ParseAuthenticateResponse(BuildSyncFailureResponse(auts));

        Assert.True(result.SyncFailure);
        Assert.Equal(14, result.Auts!.Length);
        Assert.Equal(Convert.ToHexString(auts), Convert.ToHexString(result.Auts));
    }

    [Fact]
    public void MacFailureStatusWordExplainsTheLikelyCause()
    {
        // SW=9862 is far more often "wrong UICC selected" than a carrier rejection.
        var result = HardwareAka.ParseAuthenticateResponse(FromHex("", sw: "9862"));

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("9862", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("ICCID", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void SyncFailureWithWrongAutsLengthIsRejected()
    {
        var result = HardwareAka.ParseAuthenticateResponse(Tagged("DC", Tlv(new byte[10])));
        Assert.False(result.Success);
    }

    [Fact]
    public void ShortResponseIsRejected()
    {
        var result = HardwareAka.ParseAuthenticateResponse(new byte[] { 0xDB });
        Assert.False(result.Success);
    }

    // ── GET RESPONSE chaining ────────────────────────────────────────────────

    [Fact]
    public void ChainedResponseIssuesGetResponseAndConcatenates()
    {
        var transmitted = new List<byte[]>();
        var script = new Queue<byte[]>(new[]
        {
            new byte[] { 0x61, 0x10 },   // more data available
            BuildSuccessResponse()        // delivered by GET RESPONSE
        });

        var raw = HardwareAka.TransmitWithChaining(
            command =>
            {
                transmitted.Add(command.ToArray());
                return script.Count > 0 ? script.Dequeue() : null;
            },
            HardwareAka.BuildAuthenticateApdu(Rand, Autn),
            cla: 0x01);

        Assert.NotNull(raw);
        Assert.Equal(2, transmitted.Count);

        // The follow-up must be GET RESPONSE with the channel CLA and Le from the status word.
        Assert.Equal("01C0000010", Convert.ToHexString(transmitted[1]));

        var result = HardwareAka.ParseAuthenticateResponse(raw!);
        Assert.True(result.Success);
        Assert.Equal(Convert.ToHexString(Res), Convert.ToHexString(result.Res!));
    }

    [Fact]
    public void ChainingStopsImmediatelyOnMacFailure()
    {
        var attempts = 0;
        var raw = HardwareAka.TransmitWithChaining(
            _ =>
            {
                attempts++;
                return new byte[] { 0x98, 0x62 };
            },
            HardwareAka.BuildAuthenticateApdu(Rand, Autn),
            cla: 0x00);

        // 9862 is terminal — asking for more data would only waste round trips.
        Assert.Equal(1, attempts);
        var result = HardwareAka.ParseAuthenticateResponse(raw!);
        Assert.False(result.Success);
        Assert.Contains("9862", result.ErrorMessage!, StringComparison.Ordinal);
    }

    // ── AT response parsing (modems disagree on the format) ──────────────────

    [Theory]
    [InlineData("+CGLA: 1,32,\"DB08090A\"", "+CGLA:", "DB08090A")]   // with a length field (Quectel)
    [InlineData("+CGLA: 1,\"DB08090A\"", "+CGLA:", "DB08090A")]      // without a length field
    [InlineData("+CSIM: 34,\"DB08090A\"", "+CSIM:", "DB08090A")]
    [InlineData("+CSIM: \"DB08090A\"", "+CSIM:", "DB08090A")]
    public void ExtractResponseBytesHandlesVendorFormats(string line, string prefix, string expectedHex)
    {
        var response = new AtResponse(true, new[] { line }, RawOutput: line);
        var parsed = Ec25AkaProvider.ExtractResponseBytes(response, prefix);

        Assert.NotNull(parsed);
        Assert.Equal(expectedHex, Convert.ToHexString(parsed));
    }

    // ── Full EC25 flows through a scripted modem ─────────────────────────────

    [Fact]
    public async Task Ec25UsesLogicalChannelWhenTheCardAcceptsIt()
    {
        var modem = new ScriptedAtSession();
        modem.On("AT+CCHO=\"A0000000871002\"", "+CCHO: 1\r\nOK");
        modem.OnCgla(1, _ => $"+CGLA: 1,{SuccessHex().Length},\"{SuccessHex()}\"\r\nOK");
        modem.On("AT+CCHC=1", "OK");

        var provider = new Ec25AkaProvider(modem);
        var result = await provider.AuthenticateAsync(AkaChallenge.Create(Rand, Autn));

        Assert.True(result.Success);
        Assert.Equal(Convert.ToHexString(Res), Convert.ToHexString(result.Res!));
        Assert.Equal(Convert.ToHexString(Ck), Convert.ToHexString(result.Ck!));
        Assert.Equal(Convert.ToHexString(Ik), Convert.ToHexString(result.Ik!));

        // The CLA byte must carry the logical channel number.
        Assert.NotEmpty(modem.CglaCommands);
        Assert.All(modem.CglaCommands, c => Assert.Equal(0x01, c[0]));
        Assert.Contains("AT+CCHC=1", modem.Executed);
    }

    [Fact]
    public async Task Ec25FallsBackToCsimWhenNoLogicalChannel()
    {
        var modem = new ScriptedAtSession();
        modem.On("AT+CCHO=", "ERROR");                     // both AIDs rejected
        modem.OnCsim(_ => $"+CSIM: {SuccessHex().Length},\"{SuccessHex()}\"\r\nOK");

        var provider = new Ec25AkaProvider(modem);
        var result = await provider.AuthenticateAsync(AkaChallenge.Create(Rand, Autn));

        Assert.True(result.Success);
        Assert.Equal(Convert.ToHexString(Res), Convert.ToHexString(result.Res!));
        Assert.Contains(modem.Executed, c => c.StartsWith("AT+CSIM=", StringComparison.Ordinal));
        Assert.DoesNotContain(modem.Executed, c => c.StartsWith("AT+CGLA=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ec25SurfacesMacFailureWithTheWrongCardHint()
    {
        var modem = new ScriptedAtSession();
        modem.On("AT+CCHO=", "ERROR");
        modem.OnCsim(_ => "+CSIM: 4,\"9862\"\r\nOK");

        var provider = new Ec25AkaProvider(modem);
        var result = await provider.AuthenticateAsync(AkaChallenge.Create(Rand, Autn));

        Assert.False(result.Success);
        Assert.Contains("ICCID", result.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckReadyRejectsAMismatchedIccid()
    {
        var modem = new ScriptedAtSession();
        modem.On("AT+CPIN?", "+CPIN: READY\r\nOK");
        modem.On("AT+QCCID", "+QCCID: 89860123456789012345\r\nOK");

        var provider = new Ec25AkaProvider(modem);

        Assert.False(await provider.CheckReadyAsync(expectedIccid: "89860000000000000001"));
    }

    [Fact]
    public async Task CheckReadyAcceptsTheExpectedCard()
    {
        var modem = new ScriptedAtSession();
        modem.On("AT+CPIN?", "+CPIN: READY\r\nOK");
        modem.On("AT+QCCID", "+QCCID: 89860123456789012345\r\nOK");

        var provider = new Ec25AkaProvider(modem);

        Assert.True(await provider.CheckReadyAsync(expectedIccid: "89860123456789012345"));
    }

    [Fact]
    public void IccidNormalisationUnswapsNibblePairs()
    {
        // EF-ICCID stores each pair swapped relative to the digits printed on the card.
        // 98 86 10 32 54 76 98 10 32 54  ->  89 68 01 23 45 67 89 01 23 45
        Assert.Equal("89680123456789012345", Ec25AkaProvider.NormalizeIccid("98861032547698103254"));
    }

    [Fact]
    public async Task IccidRegexMatchesEveryVendorCommand()
    {
        // +QCCID (Quectel), +CCID and +ICCID all report the same value in different forms.
        foreach (var command in new[] { "AT+QCCID", "AT+CCID", "AT+ICCID" })
        {
            var modem = new ScriptedAtSession();
            modem.On("AT+CPIN?", "+CPIN: READY\r\nOK");
            modem.On(command, "+QCCID: 89860123456789012345\r\nOK");

            var provider = new Ec25AkaProvider(modem);
            var iccid = await provider.ReadIccidAsync();

            Assert.Equal("89860123456789012345", iccid);
        }
    }

    [Fact]
    public async Task SoftwareProviderIsUsableWithoutHardware()
    {
        var provider = SoftwareAkaProvider.FromTestVectors();

        Assert.True(await provider.CheckReadyAsync());

        var result = await provider.AuthenticateAsync(AkaChallenge.Create(Rand, Autn));

        Assert.True(result.Success);
        Assert.Equal(16, result.Ck!.Length);
        Assert.Equal(16, result.Ik!.Length);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string SuccessHex() => Convert.ToHexString(BuildSuccessResponse());

    /// <summary>Length-prefixed TLV body: &lt;len&gt;&lt;value&gt;.</summary>
    private static string Tlv(byte[] value) => value.Length.ToString("X2") + Convert.ToHexString(value);

    /// <summary>Builds a complete APDU response: body followed by the status word.</summary>
    private static byte[] FromHex(string body, string sw = "9000") =>
        Convert.FromHexString(body + sw);

    /// <summary>Builds a BER-TLV tagged response: &lt;tag&gt;&lt;totalLen&gt;&lt;body&gt;&lt;SW&gt;.</summary>
    private static byte[] Tagged(string tag, string body, string sw = "9000") =>
        FromHex(tag + (body.Length / 2).ToString("X2") + body, sw);

    /// <summary>Minimal scripted AT channel standing in for an EC25.</summary>
    private sealed class ScriptedAtSession : IAtSession
    {
        private readonly List<(string Match, string Response)> _exact = new();
        private Func<byte[], string>? _cgla;
        private Func<byte[], string>? _csim;

        public bool IsOpen => true;
        public List<string> Executed { get; } = new();
        public List<byte[]> CglaCommands { get; } = new();

        public void On(string prefix, string response) => _exact.Add((prefix, response));

        public void OnCgla(int channel, Func<byte[], string> responder) =>
            _cgla = apdu =>
            {
                CglaCommands.Add(apdu.ToArray());
                return responder(apdu);
            };

        public void OnCsim(Func<byte[], string> responder) => _csim = responder;

        public Task<AtResponse> ExecuteCommandAsync(
            string command, int timeoutMs = 2000, CancellationToken ct = default)
        {
            Executed.Add(command);

            if (command.StartsWith("AT+CGLA=", StringComparison.Ordinal) && _cgla is not null)
                return Reply(_cgla(Convert.FromHexString(command.Split('"')[1])));

            if (command.StartsWith("AT+CSIM=", StringComparison.Ordinal) && _csim is not null)
                return Reply(_csim(Convert.FromHexString(command.Split('"')[1])));

            foreach (var (match, response) in _exact)
            {
                if (command.StartsWith(match, StringComparison.Ordinal))
                    return Reply(response);
            }

            return Task.FromResult(new AtResponse(false, Array.Empty<string>(), RawOutput: "ERROR"));
        }

        public Task<AtResponse> ExecutePromptCommandAsync(
            string initialCommand, string payload, int promptTimeoutMs = 3000, int completionTimeoutMs = 15000, CancellationToken ct = default)
        {
            Executed.Add(initialCommand);
            Executed.Add(payload);
            return ExecuteCommandAsync(initialCommand, promptTimeoutMs, ct);
        }

        private static Task<AtResponse> Reply(string raw)
        {
            var lines = raw.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            var success = lines.Any(l => l.Equals("OK", StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new AtResponse(success, lines, RawOutput: raw));
        }
    }
}
