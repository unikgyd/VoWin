using System.Text.Json;

namespace VoSharp.Ike.Tests;

public class IkeBackoffEvidenceTests
{
    [Fact]
    public void SharedTw3VectorsKeepZeroMissingInvalidAndDeactivatedDistinct()
    {
        using var contract = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "contracts", "ike-backoff-v1.json")));
        Assert.Equal(1, contract.RootElement.GetProperty("version").GetInt32());
        var vectors = contract.RootElement.GetProperty("vectors").EnumerateArray().ToArray();
        Assert.Equal(22, vectors.Length);
        foreach (var vector in vectors)
        {
            var result = IkeBackoffTimer.Decode(Convert.FromHexString(vector.GetProperty("dataHex").GetString()!));
            Assert.Equal(vector.GetProperty("status").GetString(), result.Status.ToString().ToLowerInvariant());
            if (vector.TryGetProperty("seconds", out var seconds))
                Assert.Equal(TimeSpan.FromSeconds(seconds.GetInt32()), result.Duration);
            else Assert.Null(result.Duration);
        }
        Assert.Equal(IkeBackoffTimerStatus.Missing, IkeBackoffTimer.Missing.Status);
        Assert.Null(IkeBackoffTimer.Missing.EncodedValue);
    }

    [Theory]
    [InlineData(10500, IkeRejectionScope.Epdg)]
    [InlineData(9002, IkeRejectionScope.PlmnApn)]
    [InlineData(9000, IkeRejectionScope.Plmn)]
    [InlineData(9001, IkeRejectionScope.Plmn)]
    [InlineData(9003, IkeRejectionScope.Plmn)]
    [InlineData(9006, IkeRejectionScope.Plmn)]
    [InlineData(11001, IkeRejectionScope.Plmn)]
    [InlineData(11011, IkeRejectionScope.Plmn)]
    public void CauseScopeRequiresCurrentAuthentication(ushort cause, IkeRejectionScope scope)
    {
        IkePayload[] reply = [Notify(cause), Notify(41041, [1, 0xAA])];
        Assert.Null(IkeNetworkRejection.Capture(reply, false));
        var rejection = Assert.IsType<IkeNetworkRejection>(IkeNetworkRejection.Capture(reply, true));
        Assert.Equal(cause, rejection.NotifyType); Assert.Equal(scope, rejection.Scope);
        Assert.Equal(TimeSpan.FromMinutes(10), rejection.BackoffTimer.Duration);
    }

    [Theory]
    [InlineData("duplicate-error")]
    [InlineData("mixed-error")]
    [InlineData("unknown-error")]
    [InlineData("cause-spi")]
    [InlineData("cause-data")]
    [InlineData("truncated-notify")]
    [InlineData("timer-only")]
    public void AmbiguousOrUnboundRepliesNeverProduceNetworkPolicy(string mode)
    {
        var reply = new List<IkePayload> { Notify(10500), Notify(41041, [1, 0xAA]) };
        switch (mode)
        {
            case "duplicate-error": reply.Add(Notify(10500)); break;
            case "mixed-error": reply.Add(Notify(24)); break;
            case "unknown-error": reply[0] = Notify(9999); break;
            case "cause-spi": reply[0] = new(IkePayloadType.Notify, [0, 1, 0x29, 0x04, 0]); break;
            case "cause-data": reply[0] = Notify(10500, [0]); break;
            case "truncated-notify": reply.Add(new(IkePayloadType.Notify, [0])); break;
            case "timer-only": reply.RemoveAt(0); break;
        }
        Assert.Null(IkeNetworkRejection.Capture(reply, true));
    }

    [Theory]
    [InlineData("missing", IkeBackoffTimerStatus.Missing)]
    [InlineData("bare", IkeBackoffTimerStatus.Invalid)]
    [InlineData("duplicate", IkeBackoffTimerStatus.Invalid)]
    [InlineData("spi", IkeBackoffTimerStatus.Invalid)]
    [InlineData("deactivated", IkeBackoffTimerStatus.Deactivated)]
    [InlineData("zero", IkeBackoffTimerStatus.Timed)]
    public void InvalidTimerCannotBecomeZeroOrLoseAnAuthenticatedCause(string mode, IkeBackoffTimerStatus status)
    {
        var reply = new List<IkePayload> { Notify(9002) };
        switch (mode)
        {
            case "bare": reply.Add(Notify(41041, [0xAA])); break;
            case "duplicate": reply.Add(Notify(41041, [1, 0xAA])); reply.Add(Notify(41041, [1, 0])); break;
            case "spi": reply.Add(new(IkePayloadType.Notify, [0, 1, 0xA0, 0x51, 0, 1, 0xAA])); break;
            case "deactivated": reply.Add(Notify(41041, [1, 0xE0])); break;
            case "zero": reply.Add(Notify(41041, [1, 0])); break;
        }
        var rejection = Assert.IsType<IkeNetworkRejection>(IkeNetworkRejection.Capture(reply, true));
        Assert.Equal(status, rejection.BackoffTimer.Status);
        if (mode == "zero") Assert.Equal(TimeSpan.Zero, rejection.BackoffTimer.Duration);
    }

    [Fact]
    public void EmptySpiProtocolIdIsIgnoredAndDiagnosticOnlyBareCompatibilityStaysSeparate()
    {
        var cause = Notify(10500); cause.Body[0] = 255;
        var timer = Notify(41041, [1, 0xAA]); timer.Body[0] = 255;
        Assert.Equal(TimeSpan.FromMinutes(10), IkeNetworkRejection.Capture([cause, timer], true)!.BackoffTimer.Duration);
        Assert.Equal("10 minutes", IkeSession.DescribeBackoffTimer([0xAA]));
        Assert.Equal(IkeBackoffTimerStatus.Invalid, IkeBackoffTimer.Decode([0xAA]).Status);
    }

    internal static IkePayload Notify(ushort type, byte[]? data = null)
        => new(IkePayloadType.Notify, [0, 0, (byte)(type >> 8), (byte)type, .. data ?? []]);
}
