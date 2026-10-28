using VoSharp.Sip;

namespace VoSharp.Tests;

public sealed class SipRegistrationRetryTests
{
    internal sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _utc = DateTimeOffset.Parse("2026-10-13T00:00:00Z");
        private long _timestamp;
        public override DateTimeOffset GetUtcNow() => _utc;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed) { _timestamp += elapsed.Ticks; _utc += elapsed; }
        public void MoveWallClock(TimeSpan delta) => _utc += delta;
    }

    [Theory]
    [InlineData("0", 0u)]
    [InlineData("120", 120u)]
    [InlineData(" 120 \t", 120u)]
    [InlineData("120 (maintenance)", 120u)]
    [InlineData("120 (nested (comment) \\) end);duration=3600", 120u)]
    [InlineData("18000;duration=3600", 18000u)]
    [InlineData("120;DURATION = 0;extension=\"a,b;\\\"c\";flag", 120u)]
    [InlineData("4294967295", uint.MaxValue)]
    public void RetryAfterAcceptsSipGrammarWithoutAddingDuration(string header, uint seconds)
    {
        Assert.True(SipRegistrationFailureInfo.TryParseRetryAfter(header, out var parsed));
        Assert.Equal(seconds, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1.5")]
    [InlineData("4294967296")]
    [InlineData("120, 300")]
    [InlineData("120oops")]
    [InlineData("120 (unclosed")]
    [InlineData("120;duration")]
    [InlineData("120;duration=-1")]
    [InlineData("120;duration=\"10\"")]
    [InlineData("120;duration=10;DURATION=20")]
    [InlineData("120;tag=\"unclosed")]
    [InlineData("120;tag=\"escaped\\")]
    [InlineData("120\r\nInjected: value")]
    public void InvalidBackoffIsNotSilentlyConvertedIntoImmediateRetry(string? header)
        => Assert.False(SipRegistrationFailureInfo.TryParseRetryAfter(header, out _));

    [Fact]
    public void DuplicateHeaderIsAmbiguousEvenWhenBothValuesAgree()
    {
        var response = new SipMessage { StatusCode = 500 };
        response.AddHeader("Retry-After", "120");
        response.AddHeader("Retry-After", "120");
        var feedback = SipRegistrationFailureInfo.FromResponse(response, true);
        Assert.True(feedback.InvalidRetryAfter);
        Assert.Null(feedback.RetryAfterSeconds);
        Assert.True(feedback.PauseAutomaticRecovery);
    }

    [Fact]
    public void WallClockJumpCannotSkipOrExtendMonotonicBackoff()
    {
        var clock = new TestClock();
        var gate = new SipRegistrationRetryGate(clock);
        gate.Observe("card-a", new(503, 120, false, true, clock.GetUtcNow().UtcDateTime));
        clock.MoveWallClock(TimeSpan.FromDays(30));
        Assert.Throws<SipRegistrationRetryBlockedException>(() => gate.ThrowIfBlocked("card-a"));
        gate.ThrowIfBlocked("card-b");
        clock.MoveWallClock(TimeSpan.FromDays(-60));
        clock.Advance(TimeSpan.FromSeconds(119));
        Assert.Throws<SipRegistrationRetryBlockedException>(() => gate.ThrowIfBlocked("card-a"));
        clock.Advance(TimeSpan.FromSeconds(1));
        gate.ThrowIfBlocked("card-a");
        Assert.Null(gate.RetryNotBeforeUtc("card-a"));
    }

    [Fact]
    public void NewFeedbackAndExplicitRetryCannotShortenExistingServerTimer()
    {
        var clock = new TestClock();
        var gate = new SipRegistrationRetryGate(clock);
        gate.Observe("card", new(503, 120, false, false, clock.GetUtcNow().UtcDateTime));
        clock.Advance(TimeSpan.FromSeconds(30));
        gate.Observe("card", new(500, 10, false, false, clock.GetUtcNow().UtcDateTime));
        gate.Observe("card", new(403, null, false, false, clock.GetUtcNow().UtcDateTime));
        Assert.True(Assert.Throws<SipRegistrationRetryBlockedException>(() => gate.ThrowIfBlocked("card")).AutomaticRecoveryPaused);
        gate.ReleasePause("card");
        Assert.False(Assert.Throws<SipRegistrationRetryBlockedException>(() => gate.ThrowIfBlocked("card")).AutomaticRecoveryPaused);
        clock.Advance(TimeSpan.FromSeconds(90));
        gate.ThrowIfBlocked("card");
    }

    [Fact]
    public async Task VeryLongWaitIsCancellableWithoutClampingTheServerTimer()
    {
        var gate = new SipRegistrationRetryGate();
        gate.Observe("card", new(503, uint.MaxValue, false, true, DateTime.UtcNow));
        using var cancellation = new CancellationTokenSource();
        var waiting = gate.WaitUntilAllowedAsync("card", cancellation.Token);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Throws<SipRegistrationRetryBlockedException>(() => gate.ThrowIfBlocked("card"));
    }

    [Fact]
    public async Task InvalidFeedbackWaitFailsClosedUntilExplicitRetry()
    {
        var gate = new SipRegistrationRetryGate();
        gate.Observe("card", new(500, null, true, true, DateTime.UtcNow));
        var error = await Assert.ThrowsAsync<SipRegistrationRetryBlockedException>(() => gate.WaitUntilAllowedAsync("card"));
        Assert.True(error.AutomaticRecoveryPaused);
        gate.ReleasePause("card");
        await gate.WaitUntilAllowedAsync("card");
    }
}
