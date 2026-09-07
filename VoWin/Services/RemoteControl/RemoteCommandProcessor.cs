using System.Text;
using System.Text.RegularExpressions;
using VoSharp.Kernel.Pool;
using VoWin.Models;

namespace VoWin.Services.RemoteControl;

internal sealed class RemoteCommandProcessor
{
    private readonly IVoKernelService _kernel;

    public RemoteCommandProcessor(IVoKernelService kernel) => _kernel = kernel;

    public async Task<string> ExecuteAsync(string input, CancellationToken ct)
    {
        var raw = input.Trim();
        if (raw.StartsWith('/')) raw = raw[1..];
        var split = raw.IndexOfAny([' ', '\t', '\r', '\n']);
        var command = (split < 0 ? raw : raw[..split]).Trim().ToLowerInvariant();
        var args = split < 0 ? string.Empty : raw[(split + 1)..].Trim();

        try
        {
            return command switch
            {
                "帮助" or "help" or "菜单" => HelpText,
                "状态" or "status" => BuildStatus(),
                "卡片" or "卡槽" or "slots" => BuildSlots(),
                "切卡" or "switch" => SwitchSlot(args),
                "验证码" or "code" or "otp" => await GetOtpAsync(args).ConfigureAwait(false),
                "短信" or "sms" => await SendSmsAsync(args).ConfigureAwait(false),
                "拨号" or "电话" or "call" => await DialAsync(args).ConfigureAwait(false),
                "挂断" or "hangup" => await HangupAsync().ConfigureAwait(false),
                "接听" or "answer" => await AnswerAsync().ConfigureAwait(false),
                "拒接" or "reject" => await RejectAsync().ConfigureAwait(false),
                "vowifi" or "wifi通话" => await SetVoWifiAsync(args).ConfigureAwait(false),
                "飞行模式" or "flight" => await SetFlightModeAsync(args).ConfigureAwait(false),
                "刷新" or "refresh" => await RefreshAsync().ConfigureAwait(false),
                _ => "未知命令。发送“帮助”查看可用命令。"
            };
        }
        catch (Exception ex)
        {
            return $"操作失败：{ex.Message}";
        }
    }

    private string BuildStatus()
    {
        var slot = _kernel.Kernel.GetActiveSlot();
        var reg = slot?.Registration;
        var signal = slot?.Signal;
        var wifi = slot?.VoWifiDiag;
        var sb = new StringBuilder("VoWin 系统状态\n");
        sb.AppendLine($"卡槽：{slot?.Name ?? "无"} ({slot?.Id ?? "-"})");
        sb.AppendLine($"模块：{slot?.State.ToString() ?? "离线"} / {slot?.PortName ?? "-"}");
        sb.AppendLine($"SIM：{MaskIccid(slot?.Sim?.Iccid)} / {slot?.Sim?.OperatorName ?? "未知运营商"}");
        sb.AppendLine($"蜂窝：{reg?.StatusDisplay ?? "未知"} {reg?.AccessTechnology ?? string.Empty}");
        sb.AppendLine($"信号：{(signal == null ? "未知" : $"{signal.Bars}/5 ({signal.RssiDbm} dBm, {signal.Rat})")}");
        sb.AppendLine($"VoWiFi：{wifi?.State.ToString() ?? "未启动"}");
        sb.AppendLine($"通话：{_kernel.CurrentCallState}{(string.IsNullOrWhiteSpace(_kernel.CurrentCallNumber) ? "" : $" ({_kernel.CurrentCallNumber})")}");
        return sb.ToString().TrimEnd();
    }

    private string BuildSlots()
    {
        var slots = _kernel.Kernel.GetSlots().ToList();
        if (slots.Count == 0) return "当前没有可用卡槽。";
        var active = _kernel.Kernel.GetActiveSlot()?.Id;
        var lines = slots.Select((s, i) =>
            $"{i + 1}. {(s.Id == active ? "●" : "○")} {s.Name} | {s.State} | {MaskIccid(s.Sim?.Iccid)} | VoWiFi {s.VoWifiDiag?.State}");
        return "卡槽列表\n" + string.Join("\n", lines) + "\n使用：切卡 <序号/卡槽ID/ICCID后四位>";
    }

    private string SwitchSlot(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "用法：切卡 <序号/卡槽ID/ICCID后四位>";
        var slots = _kernel.Kernel.GetSlots().ToList();
        ModemSlot? selected = null;
        if (int.TryParse(value, out var index) && index >= 1 && index <= slots.Count) selected = slots[index - 1];
        selected ??= slots.FirstOrDefault(s => s.Id.Equals(value, StringComparison.OrdinalIgnoreCase));
        selected ??= slots.FirstOrDefault(s => s.Sim?.Iccid?.EndsWith(value, StringComparison.OrdinalIgnoreCase) == true);
        if (selected == null) return "未找到该卡槽，发送“卡片”查看列表。";
        return _kernel.SelectSlot(selected.Id) ? $"已切换到 {selected.Name}（{MaskIccid(selected.Sim?.Iccid)}）。" : "切换失败。";
    }

    private async Task<string> GetOtpAsync(string value)
    {
        var count = int.TryParse(value, out var n) ? Math.Clamp(n, 1, 10) : 3;
        var all = await _kernel.Preferences.GetAllSmsMessagesAsync().ConfigureAwait(false);
        var rows = all.Where(x => !x.IsOutgoing && x.HasOtpCode)
            .OrderByDescending(x => x.Timestamp).Take(count).ToList();
        if (rows.Count == 0) return "暂时没有找到验证码短信。";
        return "最近验证码\n" + string.Join("\n", rows.Select(x =>
            $"{x.Timestamp:MM-dd HH:mm:ss} {x.SenderOrRecipient}：{x.ExtractedOtpCode}"));
    }

    private async Task<string> SendSmsAsync(string value)
    {
        var split = value.IndexOfAny([' ', '\t', '\r', '\n']);
        if (split <= 0) return "用法：短信 <号码> <内容>";
        var number = value[..split].Trim();
        var text = value[(split + 1)..].Trim();
        if (!ValidNumber(number) || string.IsNullOrWhiteSpace(text)) return "号码或短信内容无效。";
        if (text.Length > 1000) return "短信内容过长，最多 1000 字符。";
        var result = await _kernel.SendSmsAsync(number, text).ConfigureAwait(false);
        return result.AllPartsAccepted ? $"短信已提交到 {number}。" : $"短信发送失败：{result.SubmissionStatus}";
    }

    private async Task<string> DialAsync(string number)
    {
        if (!ValidNumber(number)) return "用法：拨号 <号码>";
        if (_kernel.CurrentCallState is not (VoSharp.Telephony.Calls.CallState.Idle or VoSharp.Telephony.Calls.CallState.Ended))
            return $"当前已有通话：{_kernel.CurrentCallState}";
        var call = await _kernel.DialAsync(number).ConfigureAwait(false);
        return $"正在使用默认语音设备呼叫 {number}，状态：{call.State}。";
    }

    private async Task<string> HangupAsync()
    {
        await _kernel.HangupAsync().ConfigureAwait(false);
        return "已请求挂断。";
    }

    private async Task<string> AnswerAsync()
    {
        var call = await _kernel.AnswerAsync().ConfigureAwait(false);
        return call == null ? "当前没有可接听的来电，或接听失败。" : "已接听，正在使用默认语音设备。";
    }

    private async Task<string> RejectAsync()
    {
        var call = await _kernel.RejectAsync().ConfigureAwait(false);
        return call == null ? "当前没有可拒接的来电，或拒接失败。" : "已拒接。";
    }

    private async Task<string> SetVoWifiAsync(string value)
    {
        if (!TryParseSwitch(value, out var enabled)) return "用法：VoWiFi 开|关";
        var ok = enabled ? await _kernel.StartVoWifiAsync().ConfigureAwait(false) : await _kernel.StopVoWifiAsync().ConfigureAwait(false);
        return ok ? $"VoWiFi 已{(enabled ? "开启" : "关闭")}。" : $"VoWiFi {(enabled ? "启动" : "停止")}失败，请查看系统日志。";
    }

    private async Task<string> SetFlightModeAsync(string value)
    {
        if (!TryParseSwitch(value, out var enabled)) return "用法：飞行模式 开|关";
        var ok = await _kernel.SetFlightModeAsync(enabled).ConfigureAwait(false);
        return ok ? $"飞行模式已{(enabled ? "开启" : "关闭")}。" : "飞行模式切换失败。";
    }

    private async Task<string> RefreshAsync()
    {
        await _kernel.RefreshMetricsAsync().ConfigureAwait(false);
        return BuildStatus();
    }

    private static bool TryParseSwitch(string value, out bool enabled)
    {
        var normalized = value.Trim().ToLowerInvariant();
        enabled = normalized is "开" or "开启" or "on" or "1" or "enable";
        return enabled || normalized is "关" or "关闭" or "off" or "0" or "disable";
    }

    private static bool ValidNumber(string value) => Regex.IsMatch(value, @"^\+?[0-9*#]{3,32}$");
    private static string MaskIccid(string? iccid) => string.IsNullOrWhiteSpace(iccid) ? "无 SIM" : $"****{iccid[^Math.Min(4, iccid.Length)..]}";

    private const string HelpText = """
        VoWin 远程命令
        状态                 查看模块、网络、VoWiFi 和通话状态
        验证码 [数量]        查看最近验证码（默认 3 条）
        短信 <号码> <内容>   发送短信
        拨号 <号码>          使用当前卡和默认语音设备拨号
        接听 / 拒接 / 挂断   控制当前通话
        卡片                 查看所有卡槽
        切卡 <序号/ID/后四位> 切换当前卡
        VoWiFi 开|关         切换 VoWiFi
        飞行模式 开|关       切换当前模块飞行模式
        刷新                 刷新网络状态
        """;
}
