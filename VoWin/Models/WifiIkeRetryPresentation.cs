using System.Globalization;
using VoSharp.Telephony.VoWifi;

namespace VoWin.Models;

/// <summary>Presentation only. Does not read a store/clock or authorize any handshake.</summary>
internal static class WifiIkeRetryPresentation
{
    internal static GatewayStatusItem Create(IkeRetryStatus status, string? diagnostic = null, bool recoveryPaused = false)
    {
        var timed = !status.UntilCardEvent && status.Remaining.HasValue &&
            status.Source is "network-timer" or "implementation-missing-timer" or "implementation-invalid-timer";
        var value = timed ? recoveryPaused ? "自动重连已暂停" : "等待 IKE 重试" : status.Source switch
        {
            "uncertain-attempt" => "握手结果待确认",
            "store-unavailable" => "重试记录不可用",
            "card-event" or "binding-unavailable" => "卡状态待核验",
            _ => "重试状态待核验"
        };
        var detail = status.Source switch
        {
            "network-timer" => "运营商要求暂缓新的 IKE 握手。",
            "implementation-missing-timer" => "运营商未提供等待时长，正在使用本地保守等待。",
            "implementation-invalid-timer" => "运营商等待时长无效，正在使用本地保守等待。",
            "uncertain-attempt" => "上次握手是否完成尚未确认；为避免重复发送，新握手已暂停。",
            "store-unavailable" => "无法确认本地重试记录或计时依据，新握手已暂停；请检查存储与诊断日志。",
            "card-event" => "运营商限制需经已验证的 SIM 生命周期事件重新核验；普通重启不会清除记录。",
            "binding-unavailable" => "当前 SIM 或尝试的归属未能确认，新握手已暂停。",
            _ => "重试状态尚未确认，新握手已暂停；请查看诊断日志。"
        };
        if (timed && status.Remaining is { } remaining)
        {
            var seconds = Math.Max(0, (long)Math.Ceiling(remaining.TotalSeconds));
            detail += seconds > 0
                ? $" 最近一次核对需等待约 {seconds.ToString(CultureInfo.InvariantCulture)} 秒（不是实时倒计时）。"
                : " 最近一次核对的时长已到边界，仍需重新核验后才能发送。";
        }
        var scope = status.Scope switch { "epdg" => "ePDG", "plmn-apn" => "运营商 / APN", "plmn" => "运营商", "card" => "当前 SIM", _ => "待核验" };
        detail += $" 作用域：{scope}" + (status.Cause.HasValue ? $"；原因码：{status.Cause.Value.ToString(CultureInfo.InvariantCulture)}。" : "。");
        detail += " 更改地址、网络或手动重试不会清空限制；重试前会再次核验。";
        if (timed && recoveryPaused) detail += " 自动重连另有暂停原因，等待结束后仍需检查诊断并重新核验。";
        if (!string.IsNullOrWhiteSpace(diagnostic)) detail += " 诊断：" + diagnostic;
        return new("VoWiFi", value, detail, timed && !recoveryPaused ? GatewayStatusTone.Information : GatewayStatusTone.Error)
        { Light = timed && !recoveryPaused ? StatusLight.Busy : StatusLight.Error, ShortStatus = timed && !recoveryPaused ? "等待重试" : "已暂停" };
    }
}
