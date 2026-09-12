using System.Text;
using VoSharp.Common.Events;
using VoSharp.Kernel;
using VoSharp.Sip;
using VoSharp.Telephony.Sms;
using VoSharp.Telephony.VoWifi;
using Xunit;

namespace VoSharp.Tests;

public class SmsComprehensiveTests
{
    [Fact]
    public void TestGsm7_BasicAndExtensionEncodingDecoding()
    {
        // Test basic characters + extended characters (€, [, ], {, }, \, |, ^, ~)
        string testText = "Hello World! Price: 10€ [Special] {test} \\pipe|^~";
        Assert.True(Gsm7Alphabet.CanEncode(testText));

        var septets = Gsm7Alphabet.EncodeToSeptets(testText);
        Assert.NotEmpty(septets);

        // Extension characters take 2 septets (0x1B escape + code)
        Assert.True(septets.Length > testText.Length);

        var decoded = Gsm7Alphabet.DecodeSeptets(septets);
        Assert.Equal(testText, decoded);

        // Test packed 7-bit encoding/decoding
        var packed = Gsm7Alphabet.Encode7Bit(testText);
        var unpackedText = Gsm7Alphabet.Decode7Bit(packed, septets.Length);
        Assert.Equal(testText, unpackedText);
    }

    [Fact]
    public void TestGsm7_SplitSeptets_NeverSplitsEscapePair()
    {
        // Build septets where an extension character (€ = [0x1B, 0x65]) falls at index 152-153
        var septetsList = new List<byte>();
        for (int i = 0; i < 152; i++) septetsList.Add(0x41); // 'A'
        septetsList.Add(0x1B); // Escape
        septetsList.Add(0x65); // €
        for (int i = 0; i < 20; i++) septetsList.Add(0x42); // 'B'

        var chunks = Gsm7Alphabet.SplitSeptets(septetsList.ToArray(), 153);
        Assert.Equal(2, chunks.Count);

        // Chunk 1 should have 152 septets (not 153, avoiding splitting 0x1B from 0x65)
        Assert.Equal(152, chunks[0].Length);
        // Chunk 2 should begin with 0x1B, 0x65
        Assert.Equal(0x1B, chunks[1][0]);
        Assert.Equal(0x65, chunks[1][1]);
    }

    [Fact]
    public void TestSmsPdu_PrepareSubmitParts_SingleAndMultiPartGsm7()
    {
        // 1. Short text -> single part
        var singleParts = SmsPdu.PrepareSubmitParts("+8613800138000", "Hello single part");
        Assert.Single(singleParts);
        Assert.Equal(1, singleParts[0].PartNumber);
        Assert.Equal(1, singleParts[0].TotalParts);
        Assert.Equal(SmsEncoding.Gsm7Bit, singleParts[0].Encoding);

        // 2. Long text (>160 septets) -> multi part with UDH
        string longText = new string('A', 200);
        var multiParts = SmsPdu.PrepareSubmitParts("+8613800138000", longText, concatReference: 42);
        Assert.Equal(2, multiParts.Count);
        Assert.Equal(1, multiParts[0].PartNumber);
        Assert.Equal(2, multiParts[0].TotalParts);
        Assert.Equal(42, multiParts[0].ConcatReference);
        Assert.Equal(2, multiParts[1].PartNumber);
        Assert.Equal(2, multiParts[1].TotalParts);
    }

    [Fact]
    public void TestSmsPdu_PrepareSubmitParts_SingleAndMultiPartUcs2()
    {
        // 1. Short Chinese text (<= 70 characters) -> single part UCS-2
        string shortChinese = "你好，这是测试短信！";
        var singleParts = SmsPdu.PrepareSubmitParts("+8613800138000", shortChinese);
        Assert.Single(singleParts);
        Assert.Equal(SmsEncoding.Ucs2, singleParts[0].Encoding);

        // 2. Long Chinese text (> 70 characters) -> multi part UCS-2
        string longChinese = new string('测', 100);
        var multiParts = SmsPdu.PrepareSubmitParts("+8613800138000", longChinese, concatReference: 88);
        Assert.Equal(2, multiParts.Count);
        Assert.Equal(1, multiParts[0].PartNumber);
        Assert.Equal(2, multiParts[0].TotalParts);
        Assert.Equal(88, multiParts[0].ConcatReference);
        Assert.Equal(SmsEncoding.Ucs2, multiParts[0].Encoding);
    }

    [Fact]
    public void TestSmsReassembler_ReassemblesOutOfOrderFragments()
    {
        var reassembler = new SmsReassembler();

        // Simulate incoming multi-part SMS arrives out-of-order: part 2 arrives before part 1
        var part2 = new IncomingSms(
            SenderNumber: "+8613800138000",
            Text: "World! Multi-part reassembly test.",
            Timestamp: DateTime.UtcNow,
            Concat: new SmsConcatInfo(Reference: 99, Total: 2, Sequence: 2)
        );

        var part1 = new IncomingSms(
            SenderNumber: "+8613800138000",
            Text: "Hello ",
            Timestamp: DateTime.UtcNow.AddSeconds(-2),
            Concat: new SmsConcatInfo(Reference: 99, Total: 2, Sequence: 1)
        );

        var res1 = reassembler.ProcessIncomingPart(part2);
        Assert.Null(res1); // Missing part 1, waiting

        var res2 = reassembler.ProcessIncomingPart(part1);
        Assert.NotNull(res2); // Complete!
        Assert.Equal("+8613800138000", res2.SenderNumber);
        Assert.Equal("Hello World! Multi-part reassembly test.", res2.Text);
    }

    [Fact]
    public void TestSmsPdu_DecodeStatusReportPdu()
    {
        // SMS-STATUS-REPORT PDU:
        // SCA: 00 (0 bytes)
        // First Octet: 02 (SMS-STATUS-REPORT)
        // TP-MR: 1A (Message Reference = 26)
        // TP-RA: 0B 91 68 31 08 10 05 F0 (+86138001500)
        // TP-SCTS: 26 90 20 12 00 00 00 (2026-09-02 12:00:00)
        // TP-DT: 26 90 20 12 00 05 00 (2026-09-02 12:00:05)
        // TP-ST: 00 (Status: 0x00 = Delivered successfully)
        string pduHex = "00021A0B916831081005F0269020120000002690201200050000";

        var decoded = SmsPdu.DecodePdu(pduHex);
        Assert.True(decoded.IsStatusReport);
        Assert.NotNull(decoded.StatusReport);

        var report = decoded.StatusReport;
        Assert.Equal(26, report.MessageReference);
        Assert.Equal("+86138001500", report.Recipient);
        Assert.Equal(0x00, report.StatusCode);
        Assert.Equal("delivered", report.DeliveryStatus);
        Assert.NotNull(report.ServiceCenterTimestamp);
        Assert.NotNull(report.DischargeTimestamp);
    }

    [Fact]
    public void TestSmsPdu_AlphanumericOriginatingAddress()
    {
        // SMS-DELIVER PDU with Alphanumeric Sender "Google":
        // SCA: 00
        // First Octet: 04 (SMS-DELIVER)
        // TP-OA: 0B D0 C7 F7 FB CC 2E 03 (0xD0 = Alphanumeric TOA, "Google")
        // TP-PID: 00
        // TP-DCS: 00
        // TP-SCTS: 26 90 20 12 00 00 00
        // TP-UDL: 05
        // TP-UD: E8 32 9B FD 06 ("hello")
        var pduHex = "00040BD0C7F7FBCC2E0300002690201200000005E8329BFD06";
        var sms = SmsPdu.DecodeDeliverPdu(pduHex);
        Assert.NotNull(sms);
        Assert.Equal("Google", sms.SenderNumber);
        Assert.Equal("hello", sms.Text);
    }

    [Fact]
    public void TestSmsPdu_IsMachinePayloadDetection()
    {
        // 1. PID 0x7F = (U)SIM Data Download
        Assert.True(SmsPdu.IsMachinePayload(0x7F, 0x00, "text"));

        // 2. Class 2 SIM message (DCS 0xF6)
        Assert.True(SmsPdu.IsMachinePayload(0x00, 0xF6, "OTA Payload"));

        // 3. Normal human text
        Assert.False(SmsPdu.IsMachinePayload(0x00, 0x00, "Hello, your verification code is 123456."));

        // 4. Binary stray unprintable control characters
        Assert.True(SmsPdu.IsMachinePayload(0x00, 0x00, "\x01\x02\x03\x04\x05\x06\x07"));
    }

    [Fact]
    public void TestImsSmsHandler_BuildAndParseRpdu()
    {
        byte refByte = 0x7E;
        string smsc = "+8613800100500";
        byte[] tpdu = [0x01, 0x00, 0x0B, 0x91, 0x68, 0x31, 0x08, 0x10, 0x05, 0xF0, 0x00, 0x00, 0x05, 0xE8, 0x32, 0x9B, 0xFD, 0x06];

        // Build RP-MO-DATA
        var rpData = ImsSmsHandler.BuildRpData(refByte, smsc, tpdu);
        Assert.NotNull(rpData);
        Assert.Equal(0x00, rpData[0]); // Type 0 = RP-MO-DATA
        Assert.Equal(refByte, rpData[1]);

        // Build RP-ACK
        var rpAck = ImsSmsHandler.BuildRpAck(refByte);
        Assert.Equal(6, rpAck.Length);
        Assert.Equal(0x02, rpAck[0]); // Type 2 = RP-ACK
        Assert.Equal(refByte, rpAck[1]);
        Assert.Equal(0x41, rpAck[2]); // IEI RP-User-Data

        // Build RP-ERROR
        var rpErr = ImsSmsHandler.BuildRpError(refByte, 95);
        Assert.Equal(4, rpErr.Length);
        Assert.Equal(0x04, rpErr[0]); // Type 4 = RP-ERROR
        Assert.Equal(95, rpErr[3]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid-smsc")]
    [InlineData("+123456789012345678901")]
    public void TestImsSmsHandler_RejectsUnverifiedRpDestination(string value)
    {
        Assert.Throws<ArgumentException>(() => ImsSmsHandler.EncodeRpAddress(value));
    }

    [Fact]
    public void TestImsSmsHandler_ExtractSmsPayload_MultipartAndDirect()
    {
        // 1. Direct application/vnd.3gpp.sms
        var msg1 = new SipMessage
        {
            IsRequest = true,
            Method = "MESSAGE",
            RequestUri = "sip:+8613800138000@ims.mnc000.mcc460.3gppnetwork.org",
            Body = "binary content"
        };
        msg1.SetHeader("Content-Type", "application/vnd.3gpp.sms");
        msg1.SetHeader("Content-Transfer-Encoding", "binary");

        var payload1 = ImsSmsHandler.ExtractSmsPayload(msg1, out var pType1);
        Assert.NotNull(payload1);
        Assert.Equal("application/vnd.3gpp.sms", pType1);

        // 2. Multipart/mixed
        string boundary = "boundary42";
        var multipartBody = $"--{boundary}\r\nContent-Type: text/plain\r\n\r\nignored\r\n--{boundary}\r\nContent-Type: application/vnd.3gpp.sms\r\nContent-Transfer-Encoding: binary\r\n\r\nSMS_PAYLOAD\r\n--{boundary}--\r\n";
        var msg2 = new SipMessage
        {
            IsRequest = true,
            Method = "MESSAGE",
            RequestUri = "sip:+8613800138000@ims.mnc000.mcc460.3gppnetwork.org",
            Body = multipartBody
        };
        msg2.SetHeader("Content-Type", $"multipart/mixed; boundary=\"{boundary}\"");

        var payload2 = ImsSmsHandler.ExtractSmsPayload(msg2, out var pType2);
        Assert.NotNull(payload2);
        Assert.Equal("multipart/mixed", pType2);
        Assert.Equal("SMS_PAYLOAD", Encoding.Latin1.GetString(payload2));
    }

    [Fact]
    public async Task TestKernel_OfflineSmsFailsClosedWithoutOutboxMutation()
    {
        await using var kernel = new VoKernel(new AsyncEventBus());

        var sendRes = await kernel.ExecuteCommandAsync("sms send +8613800138000 Hello Kernel Outbox Test");

        Assert.False(sendRes.Success);
        Assert.Contains("was not sent", sendRes.Message);
        Assert.Empty(kernel.Outbox);

        var statsRes = await kernel.ExecuteCommandAsync("sms status");
        Assert.True(statsRes.Success);
        Assert.Contains("0 Sent", statsRes.Message);
    }

    [Fact]
    public async Task TestSmsService_ProcessDecodedSms_HandlesSingleAndMultipartAndReports()
    {
        var eventBus = new AsyncEventBus();
        // Use a dummy port driver (it won't be opened)
        await using var driver = new VoSharp.Modem.ModemDriver("COM99", 115200, eventBus);
        var smsService = new SmsService(driver, eventBus);

        var receivedList = new List<SmsMessage>();
        var reportsList = new List<SmsStatusReport>();
        smsService.SmsReceived += (s, e) => receivedList.Add(e.Message);
        smsService.StatusReportReceived += (s, e) => reportsList.Add(e.Report);

        // 1. Single-part SMS
        var singleMsg = new SmsMessage(1, SmsStatus.Unread, "+8613800138000", "Hello Single", DateTime.UtcNow);
        smsService.ProcessDecodedSms(singleMsg);
        Assert.Single(receivedList);
        Assert.Equal("Hello Single", receivedList[0].Text);

        // 2. Multi-part SMS (part 1 of 2, then part 2 of 2)
        var concat = new SmsConcatInfo(Reference: 99, Total: 2, Sequence: 1);
        var part1 = new SmsMessage(2, SmsStatus.Unread, "+8613800138000", "Part One - ", DateTime.UtcNow, Concat: concat);
        smsService.ProcessDecodedSms(part1);
        // Should not have fired yet
        Assert.Single(receivedList);

        var concat2 = new SmsConcatInfo(Reference: 99, Total: 2, Sequence: 2);
        var part2 = new SmsMessage(3, SmsStatus.Unread, "+8613800138000", "Part Two!", DateTime.UtcNow, Concat: concat2);
        smsService.ProcessDecodedSms(part2);
        // Now it should have fired the stitched message!
        Assert.Equal(2, receivedList.Count);
        Assert.Equal("Part One - Part Two!", receivedList[1].Text);
        Assert.Null(receivedList[1].Concat);

        // 3. Status Report
        var statusReportMsg = new SmsMessage(
            Index: 4,
            Status: SmsStatus.Read,
            SenderOrRecipient: "+8613800138000",
            Text: "[Delivery Report]",
            Timestamp: DateTime.UtcNow,
            Direction: SmsDirection.StatusReport,
            MessageReference: 77,
            StatusCode: 0,
            DeliveryStatus: "delivered"
        );
        smsService.ProcessDecodedSms(statusReportMsg);
        Assert.Single(reportsList);
        Assert.Equal(77, reportsList[0].MessageReference);
        Assert.Equal("delivered", reportsList[0].DeliveryStatus);
    }

    [Fact]
    public void BinarySipSerializationPreservesSmsDeliverAndEmptySipResponse()
    {
        var tpdu = VoWifiSmsReceptionTests.Deliver;
        byte[] payload = [1, 0x42, 0, 0, (byte)tpdu.Length, .. tpdu];
        var request = VoWifiSmsReceptionTests.Incoming(payload: payload);
        var parsed = SipMessage.Parse(request.ToBytes());
        Assert.Equal(payload, ImsSmsHandler.ExtractSmsPayload(parsed, out _));
        var decoded = ImsSmsHandler.DecodeImsSms(payload);
        Assert.NotNull(decoded);
        Assert.Equal("hello", decoded.Text);
        var response = ImsSmsHandler.BuildSipResponse(parsed, 200, "test");
        Assert.Null(response.RawBody);
        Assert.Equal("0", response.GetHeader("Content-Length"));
    }
}
