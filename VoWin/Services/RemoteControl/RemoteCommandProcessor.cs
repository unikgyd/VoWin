using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using VoSharp.Kernel;
using VoSharp.Kernel.Pool;
using VoWin.Helpers;
using VoWin.Models;

namespace VoWin.Services.RemoteControl;

/// <summary>Conversation-local line selection; never changes the desktop's active slot.</summary>
internal sealed class RemoteCommandProcessor(IVoKernelService kernel)
{
    private readonly ConcurrentDictionary<string, string> _selectedLines = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _lineGates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<string> ExecuteAsync(string input, CancellationToken ct, string session = "local",
        RemoteControlSettings? permissions = null, bool isGroup = false, Func<string, Task>? progress = null)
    {
        permissions ??= new();
        try
        {
            ct.ThrowIfCancellationRequested();
            if (input.Length > 4096) return "命令过长，未执行。";
            var raw = input.Trim();
            string? explicitLine = null;
            if (raw.StartsWith('@'))
            {
                var end = raw.IndexOf(' ');
                if (end < 2) return "用法：@线路ID 命令，例如 @1 状态。";
                explicitLine = raw[1..end]; raw = raw[(end + 1)..].Trim();
            }
            if (raw.StartsWith('/')) raw = raw[1..];
            var split = raw.IndexOfAny([' ', '\t', '\r', '\n']);
            var verb = (split < 0 ? raw : raw[..split]).ToLowerInvariant();
            var args = split < 0 ? "" : raw[(split + 1)..].Trim();
            var command = Canonical(verb);
            if (command == "help") return HelpText;
            if (isGroup && command is not ("status" or "slots")) return "此命令仅限授权私聊，避免向群聊泄露短信或控制线路。";
            if (command == "slots") return BuildSlots();
            if (command == "line")
            {
                var selected = FindSlot(args);
                _selectedLines[session] = selected.Id;
                return $"✅ 本会话使用 {Label(selected)}。不会切换电脑或其他用户的线路。";
            }
            if (command is null) return "未知命令。发送「帮助」查看菜单；不会执行任意 AT、脚本或 eSIM 删除。";
            var denied = command switch
            {
                "sms" when !permissions.AllowRemoteSms => "发短信",
                "call" or "answer" or "reject" or "hangup" or "dtmf" when !permissions.AllowRemoteCalls => "通话控制",
                "hostims" or "bridge" or "vowifi" or "flight" or "route" or "cancel" when !permissions.AllowRemoteLineChanges => "修改线路",
                "otp" when !permissions.AllowRemoteReadSms => "读取短信和验证码",
                _ => null
            };
            if (denied != null) return $"🔒 未开启「{denied}」权限。请在电脑「控制和提醒 → 交互权限」中启用；本次未执行。";
            var slots = Slots();
            var target = explicitLine ?? (command is "status" or "details" && args.Length > 0 ? args : null)
                ?? _selectedLines.GetValueOrDefault(session);
            if (command == "status" && target == null && slots.Count != 1) return BuildSlots();
            var slot = target != null ? FindSlot(target) : slots.Count == 1 ? slots[0]
                : throw new InvalidOperationException("有多条线路，请先发送「用卡 1」或使用「@线路ID 命令」。不会默认借用电脑选中的卡。");
            if (command is "status" or "details") return BuildStatus(slot, command == "details");
            if (command is "hangup" or "reject" or "cancel" || command == "vowifi" && TrySwitch(args, out var wifiOn) && !wifiOn)
                return $"{RemoteMessageMarkdown.Escape(Label(slot))}\n\n" + await ExecuteOnLineAsync(command, args, slot, ct).ConfigureAwait(false);
            var gate = _lineGates.GetOrAdd(slot.Id, _ => new(1, 1));
            if (!await gate.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false))
                return $"⏳ {Label(slot)} 正在处理上一项操作，请稍后重试。其他线路不受影响。";
            try
            {
                ct.ThrowIfCancellationRequested();
                if (progress != null && command is "call" or "hostims" or "bridge" or "vowifi")
                    await progress($"{RemoteMessageMarkdown.Escape(Label(slot))}\n正在处理连接／呼叫；可发送「状态」查询、「挂断」结束呼叫或「取消连接」取消蜂窝连接。").ConfigureAwait(false);
                return $"**{RemoteMessageMarkdown.Escape(Label(slot))}**\n\n" + await ExecuteOnLineAsync(command, args, slot, ct).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) { return "操作已取消；未确认的短信或呼叫请先查状态，不要重复提交。"; }
        catch (Exception ex) { return RemoteMessageMarkdown.Error("操作未完成", DiagnosticLogRedactor.Redact(ex.Message)); }
    }

    internal static string? Canonical(string value) => value switch
    {
        "帮助" or "help" or "菜单" or "start" => "help",
        "状态" or "status" => "status", "详情" or "details" => "details",
        "卡片" or "卡槽" or "slots" => "slots", "切卡" or "用卡" or "switch" or "line" => "line",
        "验证码" or "code" or "otp" => "otp", "短信" or "sms" => "sms",
        "拨号" or "电话" or "call" => "call", "接听" or "answer" => "answer",
        "拒接" or "reject" => "reject", "挂断" or "hangup" => "hangup", "按键" or "dtmf" => "dtmf",
        "vowifi" or "wifi通话" => "vowifi", "hostims" or "ims" => "hostims", "桥接" or "bridge" => "bridge",
        "飞行模式" or "flight" => "flight", "默认线路" or "route" => "route",
        "取消连接" or "cancel" => "cancel", "刷新" or "refresh" => "refresh", _ => null
    };

    internal static bool CanRunAlongsidePendingAction(string input)
    {
        var raw = input.Trim();
        if (raw.StartsWith('@')) { var end = raw.IndexOf(' '); if (end < 0) return false; raw = raw[(end + 1)..].Trim(); }
        raw = raw.TrimStart('/');
        var endOfVerb = raw.IndexOfAny([' ', '\t', '\n', '\r']);
        var verb = Canonical((endOfVerb < 0 ? raw : raw[..endOfVerb]).ToLowerInvariant());
        return verb is "help" or "status" or "details" or "slots" or "line" or "hangup" or "reject" or "cancel" ||
            verb == "vowifi" && endOfVerb >= 0 && TrySwitch(raw[(endOfVerb + 1)..].Trim(), out var enabled) && !enabled;
    }

    private List<ModemSlot> Slots() => kernel.Kernel.GetSlots().OrderBy(s => s.PortName, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Id).ToList();
    private ModemSlot FindSlot(string value)
    {
        var slots = Slots();
        if (int.TryParse(value, out var index) && index >= 1 && index <= slots.Count) return slots[index - 1];
        var exact = slots.FirstOrDefault(s => s.Id.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        var matches = slots.Where(s => s.Name.Equals(value, StringComparison.OrdinalIgnoreCase) ||
            value.Length >= 4 && s.Sim?.Iccid.EndsWith(value, StringComparison.Ordinal) == true).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidOperationException(matches.Length > 1
            ? "线路匹配不唯一，请使用完整线路 ID。" : "未找到此线路。发送「卡片」查看最新 ID；不会自动换到其他 SIM。");
    }
    private static string Label(ModemSlot slot) => $"{slot.Name} · {slot.Id} · {slot.PortName}";
    private string BuildSlots()
    {
        var slots = Slots();
        if (slots.Count == 0) return "当前没有设备。请在电脑接入模组并扫描；SIP Server 可独立使用。";
        return "**线路列表**\n\n" + string.Join("\n\n", slots.Select((slot, i) =>
            $"{i + 1}. {RemoteMessageMarkdown.Escape(Label(slot))}\n" + RemoteMessageMarkdown.Escape(kernel.GetGatewayStatus(slot.Id).ListSummary)))
            + "\n\n发送「用卡 序号」只改变本会话；或用「@线路ID 命令」单次指定。";
    }
    private string BuildStatus(ModemSlot slot, bool details)
    {
        var status = kernel.GetGatewayStatus(slot.Id);
        if (details) return RemoteMessageMarkdown.Escape(Label(slot)) + "\n\n" + RemoteMessageMarkdown.Escape(status.Detail);
        var sb = new StringBuilder($"**{RemoteMessageMarkdown.Escape(Label(slot))}**\n\n");
        foreach (var layer in status.OverviewLayers.Append(status.Route))
            sb.AppendLine($"- {RemoteMessageMarkdown.Escape(layer.InlineText)}");
        sb.AppendLine($"- 通话：{slot.Calls.ActiveCall?.State.ToString() ?? (slot.HasCellularCall ? "模组通话中" : "空闲")}");
        return sb.Append("\n「详情」查看原因与技术状态；「刷新」读取最新状态。").ToString();
    }

    private async Task<string> ExecuteOnLineAsync(string command, string args, ModemSlot slot, CancellationToken ct)
    {
        switch (command)
        {
            case "refresh": await kernel.RefreshMetricsAsync(slot.Id).ConfigureAwait(false); return BuildStatus(slot, false);
            case "cancel": kernel.CancelTelephonyModeConnection(slot.Id); return "已请求取消此线路的蜂窝连接；恢复收尾可能仍在进行。独立 VoWiFi 请使用「VoWiFi 关」。";
            case "otp":
                var count = int.TryParse(args, out var n) ? Math.Clamp(n, 1, 10) : 3;
                var messages = await kernel.Preferences.GetAllSmsMessagesAsync().ConfigureAwait(false);
                var rows = messages.Where(m => m.SlotId == slot.Id && !m.IsOutgoing && m.HasOtpCode).OrderByDescending(m => m.Timestamp).Take(count).ToArray();
                return rows.Length == 0 ? "此线路暂无可归属的验证码短信。" : string.Join("\n", rows.Select(m =>
                    $"{RemoteMessageMarkdown.Code(m.ExtractedOtpCode)} · {RemoteMessageMarkdown.Code(m.SenderOrRecipient)} · {TimestampDisplayHelper.ToLocalDisplayTime(m.Timestamp):MM-dd HH:mm:ss}"));
            case "sms":
                var split = args.IndexOfAny([' ', '\t', '\r', '\n']);
                if (split <= 0) return "用法：短信 <号码> <内容>。可能产生费用。";
                var number = args[..split]; var text = args[(split + 1)..].Trim();
                if (!ValidNumber(number) || text.Length is < 1 or > 1000) return "号码或短信内容无效（最多 1000 字符），未提交。";
                var result = await kernel.SendSmsAsync(number, text, slotId: slot.Id).ConfigureAwait(false);
                return result.AllPartsAccepted ? "✅ 短信已提交，不代表对方已收到。" : $"短信提交未全部成功：{RemoteMessageMarkdown.Escape(result.SubmissionStatus)}。请检查历史，勿整条重复发送。";
            case "call":
                if (!ValidNumber(args)) return "用法：拨号 <号码>。可能产生费用。";
                if (slot.Calls.ActiveCall != null || slot.HasCellularCall) return "此线路已有通话，未再次拨号。";
                var call = await kernel.DialAsync(args, slot.Id).ConfigureAwait(false);
                return $"已发起呼叫，当前状态 {call.State}，不等于已接通。发送「挂断」结束。";
            case "answer": return await kernel.AnswerAsync(slot.Id).ConfigureAwait(false) == null ? "此线路没有可接听来电，未接听其他线路。" : "已请求本机接听；语音在 Windows 上，不通过聊天机器人传输。";
            case "reject": return await kernel.RejectAsync(slot.Id).ConfigureAwait(false) == null ? "此线路没有可拒接来电。" : "已拒接，结束此线路整通来电。";
            case "hangup": return await kernel.HangupAsync(slot.Id).ConfigureAwait(false) == null ? "此线路当前没有可挂断通话。" : "已请求挂断，请以线路状态为准。";
            case "dtmf":
                if (!Regex.IsMatch(args, "^[0-9*#]{1,32}$")) return "用法：按键 <0-9、*、#>，最多 32 位，仅用于已接通电话。";
                foreach (var digit in args) { ct.ThrowIfCancellationRequested(); if (!await kernel.SendDtmfAsync(digit, slot.Id).ConfigureAwait(false)) return "按键未全部发送，请先确认此线路已接通。"; }
                return "已发送按键。";
            case "route":
                var mode = args.ToLowerInvariant() switch { "hostims" or "ims" => TelephonyMode.HostIms,
                    "vowifi" => TelephonyMode.VoWifi, "bridge" or "桥接" => TelephonyMode.ModemBridge, _ => (TelephonyMode?)null };
                if (mode == null) return "用法：默认线路 hostims|vowifi|bridge。不会自动换卡。";
                await kernel.SetPreferredTelephonyRouteAsync(slot.Id, mode.Value).ConfigureAwait(false); return "已保存此模组的默认呼出偏好，不会自动连接或迁移现有通话。";
            case "hostims": case "bridge": case "vowifi": case "flight":
                if (!TrySwitch(args, out var enabled)) return $"用法：{command} 开|关。";
                if (command == "flight") return await kernel.SetFlightModeAsync(enabled, slot.Id).ConfigureAwait(false) ? "飞行模式已更新。" : "飞行模式未切换，请查看电脑端原因。";
                if (command == "vowifi") return (enabled ? await kernel.StartVoWifiAsync(slot.Id).ConfigureAwait(false) : await kernel.StopVoWifiAsync(slot.Id).ConfigureAwait(false))
                    ? enabled ? "VoWiFi 连接流程已完成，发送「状态」检查注册。" : "VoWiFi 已停止。" : "VoWiFi 操作未完成，发送「详情」查看原因。";
                var requested = command == "hostims" ? TelephonyMode.HostIms : TelephonyMode.ModemBridge;
                if (enabled)
                {
                    using var cancel = ct.Register(() => kernel.CancelTelephonyModeConnection(slot.Id));
                    await kernel.EnableTelephonyModeAsync(slot.Id, requested).ConfigureAwait(false);
                    return "蜂窝线路连接流程已完成，发送「状态」检查实际注册。";
                }
                if (kernel.GetTelephonyModeStatus(slot.Id).Mode != requested) return "当前蜂窝模式与请求不符，未关闭其他模式。";
                await kernel.StopTelephonyModeAsync(slot.Id).ConfigureAwait(false); return "此模组的蜂窝线路已停止；独立 VoWiFi 不受影响。";
            default: return "未支持的操作，未执行。";
        }
    }
    private static bool ValidNumber(string value) => Regex.IsMatch(value, @"^\+?[0-9]{3,32}$");
    private static bool TrySwitch(string value, out bool enabled)
    {
        var normalized = value.ToLowerInvariant(); enabled = normalized is "开" or "开启" or "on" or "1";
        return enabled || normalized is "关" or "关闭" or "off" or "0";
    }
    public const string HelpText = """
        VoWin 控制和提醒

        状态 / 详情 / 卡片 / 刷新
        用卡 <序号或ID>：仅选择本会话的线路
        @线路ID 命令：单次指定线路，例如 @1 状态

        短信 <号码> <内容> / 验证码 [1-10]
        拨号 <号码> / 接听 / 拒接 / 挂断 / 按键 <数字>
        hostims 开|关 / 桥接 开|关 / VoWiFi 开|关
        默认线路 hostims|vowifi|bridge
        飞行模式 开|关 / 取消连接

        收费操作、读取短信和修改线路需要电脑端单独授权；敏感命令仅限私聊。
        接听使用电脑音频，不把通话语音传给聊天机器人。
        Bark 只发通知，不接收命令。
        """;
}
