using VoSharp.Common.Events;
using VoSharp.Modem;
using VoSharp.Modem.At;
using Xunit;

namespace VoSharp.Tests;

public class ModemTests
{
    [Theory]
    [InlineData("+CNUM: \"\",\"+12025550123\",145", "+12025550123")]
    [InlineData("+CNUM: ,\"85255550123\",145", "+85255550123")]
    [InlineData("+CNUM: \"Line 1\",\"0917 555-0123\",129", "09175550123")]
    public void CnumParser_ReturnsNormalizedCardNumber(string response, string expected)
    {
        Assert.Equal(expected, ModemDriver.ParseOwnPhoneNumber(new[] { response, "OK" }));
    }

    [Theory]
    [InlineData("+CNUM: \"\",\"\",129")]
    [InlineData("OK")]
    public void CnumParser_ReturnsEmptyWhenCardDoesNotSupplyNumber(string response)
    {
        Assert.Equal(string.Empty, ModemDriver.ParseOwnPhoneNumber(new[] { response }));
    }

    [Theory]
    [InlineData("+CREG: 2")]
    [InlineData("+CEREG: 0")]
    [InlineData("+CGREG: 5")]
    [InlineData("+CSQ: 99,99")]
    public void RadioStatusUrc_IsRecognizedForFlightModeSuppression(string urc)
    {
        Assert.True(ModemDriver.IsRadioStatusUrc(urc));
    }

    [Theory]
    [InlineData("RING")]
    [InlineData("+CMTI: \"SM\",1")]
    [InlineData("+CPIN: READY")]
    public void NonRadioUrc_IsNotSuppressedInFlightMode(string urc)
    {
        Assert.False(ModemDriver.IsRadioStatusUrc(urc));
    }

    [Fact]
    public void ClccParserFiltersImsDataBearersAndKeepsVoiceState()
    {
        var calls = ModemDriver.ParseVoiceCalls(new[]
        {
            "+CLCC: 1,1,0,1,0,\"\",128",
            "+CLCC: 2,0,3,0,0,\"14787782300\",129",
            "+CLCC: 3,1,0,1,0,\"\",128"
        });

        var call = Assert.Single(calls);
        Assert.Equal(2, call.Index);
        Assert.True(call.IsOutgoing);
        Assert.Equal(3, call.Status);
        Assert.Equal("14787782300", call.Number);
        Assert.Equal(0, call.Mode);
    }
    [Fact]
    public void TestUrcParser_IsUrc_RecognizesValidUrcs()
    {
        Assert.True(UrcParser.IsUrc("RING"));
        Assert.True(UrcParser.IsUrc("+CLIP: \"13800138000\",145"));
        Assert.True(UrcParser.IsUrc("+CMTI: \"SM\",1"));
        Assert.True(UrcParser.IsUrc("+CMT: ,23"));
        Assert.True(UrcParser.IsUrc("+CDS: 23"));
        Assert.True(UrcParser.IsUrc("+CRING: VOICE"));
        Assert.True(UrcParser.IsUrc("SMS READY"));
        Assert.True(UrcParser.IsUrc("+CEREG: 1"));
        Assert.True(UrcParser.IsUrc("+CSQ: 25,99"));
        Assert.True(UrcParser.IsUrc("+CPIN: READY"));
        Assert.True(UrcParser.IsUrc("NO CARRIER"));

        // Commands/Normal responses should NOT be recognized as URCs
        Assert.False(UrcParser.IsUrc("OK"));
        Assert.False(UrcParser.IsUrc("AT+CSQ"));
        Assert.False(UrcParser.IsUrc("+CME ERROR: 100"));
    }

    [Theory]
    [InlineData("RING", "Call", "RING")]
    [InlineData("+CRING: VOICE", "Call", "RING")]
    [InlineData("NO DIALTONE", "Call", "NO DIALTONE")]
    public void CellularCallUrcsAreParsed(string line, string category, string parsedData)
    {
        var urc = UrcParser.Parse(line);
        Assert.NotNull(urc);
        Assert.Equal(category, urc.Category);
        Assert.Equal(parsedData, urc.ParsedData);
    }

    [Theory]
    [InlineData("AT+CPIN?", "+CPIN: READY", true)]
    [InlineData("AT+CSQ", "+CSQ: 25,99", true)]
    [InlineData("AT+CREG?", "+CREG: 0,1", true)]
    [InlineData("AT+CEREG?", "+CEREG: 0,5", true)]
    [InlineData("AT+CSQ", "+CMTI: \"SM\",1", false)]
    public void QueryResponsesRemainAttachedToTheirPendingCommand(string command, string line, bool expected)
    {
        Assert.Equal(expected, UrcParser.IsExpectedCommandResponse(command, line));
    }

    [Fact]
    public async Task TestUrcParser_DispatchesToEventBus()
    {
        await using var bus = new AsyncEventBus();
        var incomingCallTcs = new TaskCompletionSource<string>();
        var sigTcs = new TaskCompletionSource<bool>();

        bus.Subscribe(EventTopics.CallIncoming, ev =>
        {
            incomingCallTcs.TrySetResult(ev.Payload?.ToString() ?? "");
        });

        bus.Subscribe(EventTopics.ModemSignal, ev =>
        {
            sigTcs.TrySetResult(true);
        });

        var urcCall = UrcParser.Parse("+CLIP: \"13800138000\",145", bus);
        Assert.NotNull(urcCall);
        Assert.Equal("CallIncoming", urcCall.Category);

        var urcSig = UrcParser.Parse("+CSQ: 28,0", bus);
        Assert.NotNull(urcSig);
        Assert.Equal("Signal", urcSig.Category);

        var callRes = await Task.WhenAny(incomingCallTcs.Task, Task.Delay(2000));
        Assert.Same(incomingCallTcs.Task, callRes);
        Assert.Equal("13800138000", await incomingCallTcs.Task);

        var sigRes = await Task.WhenAny(sigTcs.Task, Task.Delay(2000));
        Assert.Same(sigTcs.Task, sigRes);
    }
}
