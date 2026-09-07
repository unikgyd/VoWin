using System.Text.RegularExpressions;
using VoSharp.Common.Events;
using VoSharp.Modem;

namespace VoSharp.Telephony.Sms;

public class SmsService
{
    private readonly ModemDriver _modem;
    private readonly AsyncEventBus? _eventBus;
    private readonly SmsReassembler _reassembler = new();

    public event EventHandler<SmsReceivedEventArgs>? SmsReceived;
    public event EventHandler<SmsSentEventArgs>? SmsSent;
    public event EventHandler<SmsStatusReportEventArgs>? StatusReportReceived;

    public SmsService(ModemDriver modem, AsyncEventBus? eventBus = null)
    {
        _modem = modem;
        _eventBus = eventBus;
        _modem.UrcReceived += (s, e) =>
        {
            _ = HandleUrcAsync(e.UrcLine);
        };
    }

    public async Task HandleUrcAsync(string line, CancellationToken ct = default)
    {
        line = line.Trim();
        if (string.IsNullOrEmpty(line)) return;

        // Direct delivery mode: +CMT / +CDS header followed by a PDU or text body.
        // Some LTE/IMS modems use this even after mt=1 was requested via AT+CNMI.
        if (line.StartsWith("+CMT:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("+CDS:", StringComparison.OrdinalIgnoreCase))
        {
            HandleDirectSmsUrc(line);
            return;
        }

        // 1. +CMTI: "<storage>", <index> (New SMS message stored on SIM / ME)
        var cmtiMatch = Regex.Match(line, @"\+CMTI:\s*""([^""]+)"",\s*(\d+)");
        if (cmtiMatch.Success && int.TryParse(cmtiMatch.Groups[2].Value, out int cmtiIndex))
        {
            try
            {
                var msg = await ReadSmsAsync(cmtiIndex, ct).ConfigureAwait(false);
                if (msg != null)
                {
                    ProcessDecodedSms(msg);
                    _ = DeleteSmsAsync(cmtiIndex, ct);
                }
            }
            catch (Exception ex)
            {
                _eventBus?.Publish(EventTopics.SystemError, "SmsService", $"Failed to read incoming SMS #{cmtiIndex}: {ex.Message}");
            }
            return;
        }

        // 2. +CDSI: "<storage>", <index> (New SMS status delivery report)
        var cdsiMatch = Regex.Match(line, @"\+CDSI:\s*""([^""]+)"",\s*(\d+)");
        if (cdsiMatch.Success && int.TryParse(cdsiMatch.Groups[2].Value, out int cdsiIndex))
        {
            try
            {
                var msg = await ReadSmsAsync(cdsiIndex, ct).ConfigureAwait(false);
                if (msg != null)
                {
                    ProcessDecodedSms(msg);
                    _ = DeleteSmsAsync(cdsiIndex, ct);
                }
            }
            catch (Exception ex)
            {
                _eventBus?.Publish(EventTopics.SystemError, "SmsService", $"Failed to read delivery status report #{cdsiIndex}: {ex.Message}");
            }
            return;
        }
    }

    private void HandleDirectSmsUrc(string urc)
    {
        try
        {
            var lines = urc.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2) return;

            var header = lines[0];
            var payload = string.Concat(lines.Skip(1)).Trim();
            if (Regex.IsMatch(payload, @"^[0-9A-Fa-f]{10,}$") && payload.Length % 2 == 0)
            {
                IncomingSms decoded;
                try
                {
                    decoded = SmsPdu.DecodePdu(payload);
                }
                catch
                {
                    // Some modems omit the SMSC-length octet from direct +CMT/+CDS
                    // notifications and expose only the TPDU.
                    decoded = SmsPdu.DecodePdu("00" + payload);
                }
                if (decoded.IsStatusReport && decoded.StatusReport != null)
                {
                    NotifyStatusReportReceived(decoded.StatusReport);
                    _eventBus?.Publish(EventTopics.SmsStatusReport, "SmsService", decoded.StatusReport);
                    return;
                }

                ProcessDecodedSms(new SmsMessage(
                    Index: 0,
                    Status: SmsStatus.Unread,
                    SenderOrRecipient: decoded.SenderNumber,
                    Text: decoded.Text,
                    Timestamp: decoded.Timestamp,
                    RawPdu: payload,
                    Direction: SmsDirection.Received,
                    ServiceCenterTimestamp: decoded.ServiceCenterTimestamp,
                    Concat: decoded.Concat));
                return;
            }

            // Text-mode fallback: +CMT: "sender",... followed by message text.
            if (header.StartsWith("+CMT:", StringComparison.OrdinalIgnoreCase))
            {
                var senderMatch = Regex.Match(header, @"\+CMT:\s*""([^""]+)""");
                var sender = senderMatch.Success ? senderMatch.Groups[1].Value : "Unknown";
                ProcessDecodedSms(new SmsMessage(
                    Index: 0,
                    Status: SmsStatus.Unread,
                    SenderOrRecipient: sender,
                    Text: string.Join("\n", lines.Skip(1)),
                    Timestamp: DateTime.UtcNow));
            }
        }
        catch (Exception ex)
        {
            _eventBus?.Publish(EventTopics.SystemError, "SmsService", $"Failed to decode direct SMS URC: {ex.Message}");
        }
    }

    public void ProcessDecodedSms(SmsMessage msg)
    {
        if (msg.Direction == SmsDirection.StatusReport && msg.MessageReference.HasValue)
        {
            var rep = new SmsStatusReport(
                MessageReference: msg.MessageReference.Value,
                Recipient: msg.SenderOrRecipient,
                StatusCode: msg.StatusCode ?? 0,
                DeliveryStatus: msg.DeliveryStatus ?? "delivered",
                ServiceCenterTimestamp: msg.ServiceCenterTimestamp,
                DischargeTimestamp: msg.DischargeTimestamp,
                Timestamp: msg.Timestamp,
                RawPdu: msg.RawPdu
            );
            NotifyStatusReportReceived(rep);
            _eventBus?.Publish(EventTopics.SmsStatusReport, "SmsService", rep);
            return;
        }

        // Check if multi-part concatenated SMS
        if (msg.Concat != null && msg.Concat.Total > 1)
        {
            var incomingPart = new IncomingSms(
                SenderNumber: msg.SenderOrRecipient,
                Text: msg.Text,
                Timestamp: msg.Timestamp,
                ServiceCenterTimestamp: msg.ServiceCenterTimestamp,
                Encoding: SmsEncoding.Gsm7Bit,
                Concat: msg.Concat,
                RawPdu: msg.RawPdu
            );

            var reassembled = _reassembler.ProcessIncomingPart(incomingPart);
            if (reassembled == null)
            {
                // Multi-part SMS: awaiting remaining fragments
                return;
            }

            // All parts arrived! Update message with stitched text
            msg = msg with { Text = reassembled.Text, Concat = null };
        }

        NotifySmsReceived(msg);
        _eventBus?.Publish(EventTopics.SmsReceived, "SmsService", msg);
    }

    public void NotifySmsReceived(SmsMessage message)
    {
        try { SmsReceived?.Invoke(this, new SmsReceivedEventArgs(message)); } catch { }
    }

    public void NotifyStatusReportReceived(SmsStatusReport report)
    {
        try { StatusReportReceived?.Invoke(this, new SmsStatusReportEventArgs(report)); } catch { }
    }

    /// <summary>
    /// Submits an SMS message over Cellular AT modem.
    /// Supports multi-part concatenation and requests delivery status reports by default.
    /// </summary>
    public async Task<SmsSubmitResult> SendSmsDetailedAsync(
        string destinationNumber,
        string text,
        bool requestStatusReport = true,
        CancellationToken ct = default)
    {
        var parts = SmsPdu.PrepareSubmitParts(destinationNumber, text, requestStatusReport: requestStatusReport);
        var now = DateTime.UtcNow;
        var result = new SmsSubmitResult(
            Recipient: destinationNumber,
            Text: text,
            Encoding: parts[0].Encoding,
            ConcatReference: parts[0].ConcatReference,
            PartsTotal: parts.Count,
            PartsAccepted: 0,
            PartsAttempted: 0,
            AllPartsAccepted: false,
            SubmissionStatus: "pending",
            PartResults: new List<SmsSubmitPartStatus>(),
            SubmittedAt: now
        );

        // Ensure 3GPP PDU mode
        await _modem.SendRawAtCommandAsync("AT+CMGF=0", 1000, ct).ConfigureAwait(false);

        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            result = result with { PartsAttempted = result.PartsAttempted + 1 };

            var sendResp = await _modem.SendPduWithPromptAsync(part.TpduLength, part.PduHex, ct).ConfigureAwait(false);

            int? allocatedMr = null;
            if (sendResp.Success)
            {
                var mrMatch = Regex.Match(sendResp.RawOutput ?? string.Empty, @"\+CMGS:\s*(\d+)");
                if (mrMatch.Success && int.TryParse(mrMatch.Groups[1].Value, out int mrVal))
                {
                    allocatedMr = mrVal;
                }
            }

            var partStatus = new SmsSubmitPartStatus(
                Part: part.PartNumber,
                Total: part.TotalParts,
                Reference: allocatedMr ?? part.Reference,
                Accepted: sendResp.Success,
                AtResponse: sendResp.RawOutput?.Trim(),
                SubmissionStatus: sendResp.Success ? "accepted_by_baseband" : "rejected_by_baseband",
                SubmittedAt: DateTime.UtcNow
            );
            result.PartResults.Add(partStatus);

            if (partStatus.Accepted)
            {
                result = result with { PartsAccepted = result.PartsAccepted + 1 };
            }
            else
            {
                result = result with { SubmissionStatus = "failed" };
                break;
            }
        }

        if (result.PartsAccepted == result.PartsTotal)
        {
            result = result with { AllPartsAccepted = true, SubmissionStatus = "accepted_by_baseband" };
            try { SmsSent?.Invoke(this, new SmsSentEventArgs(result)); } catch { }
            _eventBus?.Publish(EventTopics.SmsSent, "SmsService", result);
        }

        return result;
    }

    public async Task<bool> SendSmsAsync(string destinationNumber, string text, CancellationToken ct = default)
    {
        var res = await SendSmsDetailedAsync(destinationNumber, text, requestStatusReport: true, ct: ct).ConfigureAwait(false);
        return res.AllPartsAccepted;
    }

    public async Task<List<SmsMessage>> ListSmsAsync(SmsStatus filter = SmsStatus.All, CancellationToken ct = default)
    {
        var list = new List<SmsMessage>();
        var storages = new[] { "\"SM\",\"SM\",\"SM\"", "\"ME\",\"ME\",\"ME\"" };

        foreach (var storage in storages)
        {
            await _modem.SendRawAtCommandAsync($"AT+CPMS={storage}", 2000, ct).ConfigureAwait(false);

            // 1. Try 3GPP PDU mode first
            await _modem.SendRawAtCommandAsync("AT+CMGF=0", 1000, ct).ConfigureAwait(false);
            int statCode = filter switch
            {
                SmsStatus.Unread => 0,
                SmsStatus.Read => 1,
                SmsStatus.Unsent => 2,
                SmsStatus.Sent => 3,
                _ => 4
            };

            var resp = await _modem.SendRawAtCommandAsync($"AT+CMGL={statCode}", 8000, ct).ConfigureAwait(false);
            if (resp.Success)
            {
                for (int i = 0; i < resp.Lines.Count; i++)
                {
                    var line = resp.Lines[i].Trim();
                    var match = Regex.Match(line, @"\+CMGL:\s*(\d+),\s*(\d+)");
                    if (match.Success)
                    {
                        int index = int.Parse(match.Groups[1].Value);
                        int sCode = int.Parse(match.Groups[2].Value);
                        var status = sCode switch
                        {
                            0 => SmsStatus.Unread,
                            1 => SmsStatus.Read,
                            2 => SmsStatus.Unsent,
                            3 => SmsStatus.Sent,
                            _ => SmsStatus.Read
                        };

                        while (++i < resp.Lines.Count)
                        {
                            var pduCandidate = resp.Lines[i].Trim();
                            if (string.IsNullOrEmpty(pduCandidate) || pduCandidate.Equals("OK", StringComparison.OrdinalIgnoreCase))
                                continue;

                            if (Regex.IsMatch(pduCandidate, @"^[0-9A-Fa-f]{10,}$"))
                            {
                                try
                                {
                                    var decoded = SmsPdu.DecodePdu(pduCandidate);
                                    if (decoded.IsStatusReport && decoded.StatusReport != null)
                                    {
                                        var rep = decoded.StatusReport;
                                        list.Add(new SmsMessage(
                                            Index: index,
                                            Status: status,
                                            SenderOrRecipient: rep.Recipient,
                                            Text: decoded.Text,
                                            Timestamp: rep.Timestamp,
                                            RawPdu: pduCandidate,
                                            Direction: SmsDirection.StatusReport,
                                            ServiceCenterTimestamp: rep.ServiceCenterTimestamp,
                                            DischargeTimestamp: rep.DischargeTimestamp,
                                            MessageReference: rep.MessageReference,
                                            StatusCode: rep.StatusCode,
                                            DeliveryStatus: rep.DeliveryStatus
                                        ));
                                    }
                                    else if (!list.Any(m => m.Index == index && m.Text == decoded.Text))
                                    {
                                        list.Add(new SmsMessage(
                                            Index: index,
                                            Status: status,
                                            SenderOrRecipient: decoded.SenderNumber,
                                            Text: decoded.Text,
                                            Timestamp: decoded.Timestamp,
                                            RawPdu: pduCandidate,
                                            Direction: SmsDirection.Received,
                                            ServiceCenterTimestamp: decoded.ServiceCenterTimestamp,
                                            Concat: decoded.Concat
                                        ));
                                    }
                                }
                                catch
                                {
                                    if (!list.Any(m => m.Index == index))
                                    {
                                        list.Add(new SmsMessage(index, status, "Unknown", $"[Raw PDU: {pduCandidate}]", DateTime.UtcNow, pduCandidate));
                                    }
                                }
                                break;
                            }
                            else if (pduCandidate.StartsWith("+CMGL:"))
                            {
                                i--;
                                break;
                            }
                        }
                    }
                }
            }

            // 2. Fallback to Text mode if needed
            if (list.Count == 0)
            {
                await _modem.SendRawAtCommandAsync("AT+CMGF=1", 1000, ct).ConfigureAwait(false);
                var textResp = await _modem.SendRawAtCommandAsync($"AT+CMGL=\"{(filter == SmsStatus.Unread ? "REC UNREAD" : "ALL")}\"", 8000, ct).ConfigureAwait(false);
                if (textResp.Success)
                {
                    for (int i = 0; i < textResp.Lines.Count; i++)
                    {
                        var line = textResp.Lines[i].Trim();
                        var match = Regex.Match(line, @"\+CMGL:\s*(\d+),\s*""([^""]+)"",\s*""([^""]+)""");
                        if (match.Success && i + 1 < textResp.Lines.Count)
                        {
                            int index = int.Parse(match.Groups[1].Value);
                            var statStr = match.Groups[2].Value;
                            var sender = match.Groups[3].Value;
                            var msgText = textResp.Lines[++i].Trim();
                            var status = statStr.Contains("UNREAD", StringComparison.OrdinalIgnoreCase) ? SmsStatus.Unread : SmsStatus.Read;
                            if (!list.Any(m => m.Index == index && m.Text == msgText))
                            {
                                list.Add(new SmsMessage(index, status, sender, msgText, DateTime.UtcNow));
                            }
                        }
                    }
                }
            }
        }

        return list;
    }

    public async Task<SmsMessage?> ReadSmsAsync(int index, CancellationToken ct = default)
    {
        var storages = new[] { "\"SM\",\"SM\",\"SM\"", "\"ME\",\"ME\",\"ME\"" };
        foreach (var storage in storages)
        {
            await _modem.SendRawAtCommandAsync($"AT+CPMS={storage}", 1000, ct).ConfigureAwait(false);

            await _modem.SendRawAtCommandAsync("AT+CMGF=0", 1000, ct).ConfigureAwait(false);
            var resp = await _modem.SendRawAtCommandAsync($"AT+CMGR={index}", 4000, ct).ConfigureAwait(false);
            if (resp.Success)
            {
                for (int i = 0; i < resp.Lines.Count; i++)
                {
                    var match = Regex.Match(resp.Lines[i], @"\+CMGR:\s*(\d+)");
                    if (match.Success)
                    {
                        int statCode = int.Parse(match.Groups[1].Value);
                        var status = statCode == 0 ? SmsStatus.Unread : SmsStatus.Read;

                        while (++i < resp.Lines.Count)
                        {
                            var pduHex = resp.Lines[i].Trim();
                            if (Regex.IsMatch(pduHex, @"^[0-9A-Fa-f]{10,}$"))
                            {
                                try
                                {
                                    var decoded = SmsPdu.DecodePdu(pduHex);
                                    if (decoded.IsStatusReport && decoded.StatusReport != null)
                                    {
                                        var rep = decoded.StatusReport;
                                        return new SmsMessage(
                                            Index: index,
                                            Status: status,
                                            SenderOrRecipient: rep.Recipient,
                                            Text: decoded.Text,
                                            Timestamp: rep.Timestamp,
                                            RawPdu: pduHex,
                                            Direction: SmsDirection.StatusReport,
                                            ServiceCenterTimestamp: rep.ServiceCenterTimestamp,
                                            DischargeTimestamp: rep.DischargeTimestamp,
                                            MessageReference: rep.MessageReference,
                                            StatusCode: rep.StatusCode,
                                            DeliveryStatus: rep.DeliveryStatus
                                        );
                                    }
                                    return new SmsMessage(
                                        Index: index,
                                        Status: status,
                                        SenderOrRecipient: decoded.SenderNumber,
                                        Text: decoded.Text,
                                        Timestamp: decoded.Timestamp,
                                        RawPdu: pduHex,
                                        Direction: SmsDirection.Received,
                                        ServiceCenterTimestamp: decoded.ServiceCenterTimestamp,
                                        Concat: decoded.Concat
                                    );
                                }
                                catch
                                {
                                    return new SmsMessage(index, status, "Unknown", $"[Raw PDU: {pduHex}]", DateTime.UtcNow, pduHex);
                                }
                            }
                        }
                    }
                }
            }

            // Fallback to text mode
            await _modem.SendRawAtCommandAsync("AT+CMGF=1", 1000, ct).ConfigureAwait(false);
            var tResp = await _modem.SendRawAtCommandAsync($"AT+CMGR={index}", 4000, ct).ConfigureAwait(false);
            if (tResp.Success && tResp.Lines.Count >= 2)
            {
                var meta = tResp.Lines[0];
                var body = string.Join("\n", tResp.Lines.Skip(1).Where(l => !l.Equals("OK", StringComparison.OrdinalIgnoreCase)));
                var match = Regex.Match(meta, @"\+CMGR:\s*""([^""]+)"",\s*""([^""]+)""");
                if (match.Success)
                {
                    var sender = match.Groups[2].Value;
                    return new SmsMessage(index, SmsStatus.Read, sender, body, DateTime.UtcNow);
                }
            }
        }

        return null;
    }

    public async Task<bool> DeleteSmsAsync(int index, CancellationToken ct = default)
    {
        string cmd = index == 0 ? "AT+CMGD=1,4" : $"AT+CMGD={index}";
        var resp = await _modem.SendRawAtCommandAsync(cmd, 3000, ct).ConfigureAwait(false);
        return resp.Success;
    }
}
