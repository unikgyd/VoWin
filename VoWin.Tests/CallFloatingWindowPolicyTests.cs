using VoSharp.Telephony.Calls;
using VoWin.Services;

namespace VoWin.Tests;

public sealed class CallFloatingWindowPolicyTests
{
    [Theory]
    [InlineData(CallState.Active)] [InlineData(CallState.Held)]
    [InlineData(CallState.Dialing)] [InlineData(CallState.Ringing)]
    public void LeavingPhonePageShowsOverlayWithoutMinimizingTheMainWindow(CallState state)
    {
        Assert.True(CallFloatingWindowPolicy.ShouldShow(state, true, true, false, false));
        Assert.False(CallFloatingWindowPolicy.ShouldShow(state, true, true, false, true));
        Assert.True(CallFloatingWindowPolicy.ShouldShow(state, true, false, false, true));
        Assert.True(CallFloatingWindowPolicy.ShouldShow(state, true, true, true, true));
        Assert.False(CallFloatingWindowPolicy.ShouldShow(state, false, true, false, false));
    }

    [Theory]
    [InlineData(CallState.Idle)] [InlineData(CallState.Incoming)] [InlineData(CallState.Ended)]
    public void IncomingAndEndedCallsDoNotLeaveAnActiveCallOverlay(CallState state) =>
        Assert.False(CallFloatingWindowPolicy.ShouldShow(state, true, false, true, false));

    [Fact]
    public void DurationDoesNotDependOnPhoneViewModelBeingConstructed()
    {
        var start = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("01:05", CallFloatingWindowPolicy.Duration(CallState.Active, start, start.AddSeconds(65)));
        Assert.Equal("01:05", CallFloatingWindowPolicy.Duration(CallState.Held, start, start.AddSeconds(65)));
        Assert.Equal("00:00", CallFloatingWindowPolicy.Duration(CallState.Active, start, start.AddSeconds(-1)));
        Assert.Equal("等待接听", CallFloatingWindowPolicy.Duration(CallState.Ringing, null, start));
    }
}
