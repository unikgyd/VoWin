using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using VoSharp.GatewayHost;

namespace VoSharp.Tests;

public sealed class GatewayHostTests
{
    [Fact]
    public void SmsPermissionsAndCallbackRequireExplicitLocalConfiguration()
    {
        var accounts = new Dictionary<string, string> { ["1001"] = "synthetic" };
        var id = "modem-" + new string('a', 64);
        var valid = new GatewaySmsConfiguration(true, true, id,
            new() { [id] = new(true, true, "1001") }, new() { ["1001"] = new(true, id) });
        valid.Validate(accounts, true);
        Assert.Throws<ArgumentException>(() => valid.Validate(accounts, false));
        Assert.Throws<ArgumentException>(() => (valid with { Permissions = new() }).Validate(accounts, true));
        Assert.Throws<ArgumentException>(() => (valid with { DefaultLine = "unconfigured" }).Validate(accounts, true));
        Assert.Throws<ArgumentException>(() => new GatewaySmsCallback(80, new string('a', 64)).Validate());
        var callback = new GatewaySmsCallback(10000, new string('a', 64)); callback.Validate();
        Assert.DoesNotContain(new string('a', 64), callback.ToString());
    }
    [Fact]
    public async Task NoAutomaticListenerAndUnavailableCapabilitiesAreNotAdvertised()
    {
        using var output = new StringWriter();
        Assert.Equal(0, await GatewayHostSession.RunAsync(new StringReader("{\"version\":1,\"id\":\"status\",\"method\":\"status\"}\n"), output));
        var frames = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.Equal("ready", frames[0].RootElement.GetProperty("event").GetString());
            Assert.DoesNotContain("ims", frames[0].RootElement.GetProperty("capabilities").GetRawText());
            var status = frames[1].RootElement.GetProperty("result");
            Assert.False(status.GetProperty("running").GetBoolean());
            Assert.False(status.GetProperty("carrierCalls").GetBoolean());
            Assert.False(status.GetProperty("carrierSms").GetBoolean());
        }
        finally { foreach (var frame in frames) frame.Dispose(); }
    }

    [Fact]
    public async Task ExplicitStartStopAndEofReleaseTheSocketAndNeverReturnCredentials()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port; probe.Dispose();
        const string secret = "synthetic-never-returned-secret";
        var start = JsonSerializer.Serialize(new { version = 1, id = "start", method = "start", @params = new {
            bindAddress = "127.0.0.1", sipPort = port, realm = "lab.local", accounts = new Dictionary<string,string> { ["1001"] = secret }, messagesEnabled = true,
        }});
        using var output = new StringWriter();
        var input = start + "\n{\"version\":1,\"id\":\"stop\",\"method\":\"stop\"}\n";
        Assert.Equal(0, await GatewayHostSession.RunAsync(new StringReader(input), output));
        Assert.DoesNotContain(secret, output.ToString());
        Assert.DoesNotContain("accounts", output.ToString());
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var started = JsonDocument.Parse(lines[1]); using var stopped = JsonDocument.Parse(lines[2]);
        Assert.True(started.RootElement.GetProperty("result").GetProperty("running").GetBoolean());
        Assert.False(stopped.RootElement.GetProperty("result").GetProperty("running").GetBoolean());
        using var rebound = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        using var eofOutput = new StringWriter();
        rebound.Dispose();
        Assert.Equal(0, await GatewayHostSession.RunAsync(new StringReader(start + "\n"), eofOutput));
        using var afterEof = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
    }

    [Fact]
    public async Task MalformedUnknownAndFutureProtocolRequestsFailWithoutEchoingInput()
    {
        const string secret = "synthetic-secret-must-not-leak";
        var input = "not-json-" + secret + "\n" +
            "{\"version\":2,\"id\":\"test\",\"method\":\"status\"}\n" +
            "{\"version\":1,\"id\":\"test\",\"method\":\"exec\",\"params\":{\"secret\":\"" + secret + "\"}}\n";
        using var output = new StringWriter();
        Assert.Equal(0, await GatewayHostSession.RunAsync(new StringReader(input), output));
        Assert.DoesNotContain(secret, output.ToString());
        Assert.Contains("invalid_request", output.ToString()); Assert.Contains("unsupported_method", output.ToString());
    }
}
