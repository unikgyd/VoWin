namespace VoWin.Models;

/// <summary>Metadata only. Never contains microphone samples or subscriber identities.</summary>
public sealed record MicrophoneUplinkStatus
{
    public string? CallId { get; init; }
    public string? SlotId { get; init; }
    public string DeviceName { get; init; } = "未打开麦克风";
    public bool IsCapturing { get; init; }
    public bool IsMuted { get; init; }
    public bool IsMessagePlayback { get; init; }
    public long CapturedBuffers { get; init; }
    public long ForwardedBuffers { get; init; }
    public long RejectedBuffers { get; init; }
    public long ProcessingErrors { get; init; }
    public int InputPeak { get; init; }
    public TimeSpan SessionAge { get; init; }
    public TimeSpan DataAge { get; init; }
    public string? Error { get; init; }
    public ulong EncodedFrames { get; init; }
    public ulong AudioPacketsSent { get; init; }
    public ulong EncodeErrors { get; init; }
    public ulong SendErrors { get; init; }

    public bool HasSession => CallId != null;
    public double InputLevel => !IsCapturing || IsMuted || DataAge > TimeSpan.FromMilliseconds(600) || InputPeak == 0 ? 0
        : Math.Clamp(100 + 20 * Math.Log10(InputPeak / 32768d) / 60 * 100, 0, 100);
    public bool CanRestart => HasSession && (IsMessagePlayback || !IsCapturing || Error != null || DataAge > TimeSpan.FromSeconds(2)
        || ProcessingErrors > 0 || RejectedBuffers > 0 || EncodeErrors > 0 || SendErrors > 0
        || SessionAge > TimeSpan.FromSeconds(2) && ForwardedBuffers > 0 && AudioPacketsSent == 0);
    public string StatusText => !HasSession ? "等待 IMS 通话"
        : Error != null ? $"{(IsMessagePlayback ? "自动应答录音播放失败" : "麦克风上行异常")}：{Error}"
        : !IsCapturing ? IsMessagePlayback ? "自动应答录音已停止" : "麦克风采集已停止"
        : IsMuted ? IsMessagePlayback ? "自动应答录音已静音" : "麦克风已静音"
        : SessionAge > TimeSpan.FromSeconds(2) && CapturedBuffers == 0 ? IsMessagePlayback ? "未读到自动应答录音数据" : "未收到麦克风数据"
        : DataAge > TimeSpan.FromSeconds(2) ? IsMessagePlayback ? "自动应答录音数据中断" : "麦克风数据中断"
        : ProcessingErrors > 0 && ForwardedBuffers == 0 ? "采集正常，但音频处理失败"
        : RejectedBuffers > 0 && ForwardedBuffers == 0 ? "采集正常，但当前通话未接收 PCM"
        : EncodeErrors > 0 ? "上行音频编码异常"
        : SendErrors > 0 ? "上行 RTP 发送异常"
        : SessionAge > TimeSpan.FromSeconds(2) && ForwardedBuffers > 0 && AudioPacketsSent == 0 ? "已采集，尚无上行音频包"
        : CapturedBuffers == 0 ? IsMessagePlayback ? "正在读取自动应答录音…" : "正在等待麦克风数据…"
        : IsMessagePlayback ? "正在向对端播放自动应答录音"
        : InputPeak < 32 ? "输入音量很小，请检查麦克风位置"
        : AudioPacketsSent > 0 ? "上行音频已发出（本机）"
        : "已采集声音，正在检查上行…";
    public string DetailText => $"{(IsMessagePlayback ? "读取" : "采集")} {CapturedBuffers} · 转交 {ForwardedBuffers} · 拒绝 {RejectedBuffers} · 编码 {EncodedFrames} · 音频包 {AudioPacketsSent} · 错误 {ProcessingErrors + (long)EncodeErrors + (long)SendErrors}";
}
