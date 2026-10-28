using VoSharp.Telephony.Calls;

namespace VoWin.Services;

internal static class CallFloatingWindowPolicy
{
    public static bool HasOngoingCall(CallState state, bool hasCall) => hasCall &&
        state is CallState.Active or CallState.Held or CallState.Dialing or CallState.Ringing;

    public static bool ShouldShow(CallState state, bool hasCall, bool mainVisible, bool minimized, bool phonePageVisible) =>
        HasOngoingCall(state, hasCall) && (!mainVisible || minimized || !phonePageVisible);

    public static string Duration(CallState state, DateTime? connectedAt, DateTime now)
    {
        if (connectedAt is { } start && state is CallState.Active or CallState.Held)
        {
            var elapsed = now > start ? now - start : TimeSpan.Zero;
            return $"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}";
        }
        return state switch { CallState.Dialing => "正在呼叫", CallState.Ringing => "等待接听", CallState.Held => "通话已保持", _ => "通话中" };
    }
}
