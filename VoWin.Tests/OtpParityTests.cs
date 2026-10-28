using System.IO;
using System.Text.Json;
using System.Text;
using VoWin.Helpers;
using VoWin.Models;
using VoWin.Services;
using VoSharp.Telephony.Sms;

namespace VoWin.Tests;

public sealed class OtpParityTests
{
    [Fact]
    public void SharedKeywordVectorsMatchTheWindowsSqlPredicate()
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "sms-api-query-v1.json")));
        foreach (var test in contract.RootElement.GetProperty("keywordCases").EnumerateArray())
            Assert.Equal(test.GetProperty("matches").GetBoolean(), test.GetProperty("text").GetString()!.Contains(test.GetProperty("keyword").GetString()!, StringComparison.OrdinalIgnoreCase));
    }
    [Fact]
    public void SmsApiTruncationPreservesSupplementaryCharactersAtTheBoundary()
    {
        Assert.Equal(new string('x', 4095), SmsApiRequestProcessor.TruncateText(new string('x', 4095) + "😀tail"));
        var exact = string.Concat(Enumerable.Repeat("😀", 2048));
        Assert.Equal(exact, SmsApiRequestProcessor.TruncateText(exact));
    }
    [Fact]
    public void SharedSmsApiQueryContractMatchesLinux()
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "sms-api-query-v1.json")));
        var now = contract.RootElement.GetProperty("now").GetDateTimeOffset();
        foreach (var test in contract.RootElement.GetProperty("cases").EnumerateArray())
        {
            SmsInboxQuery? query = null;
            var error = Record.Exception(() => query = SmsApiRequestProcessor.ParseQuery(test.GetProperty("raw").GetString()!, test.GetProperty("otp").GetBoolean(), now));
            Assert.True((error == null) == test.GetProperty("valid").GetBoolean(), test.GetProperty("name").GetString());
            if (error != null) continue;
            if (test.TryGetProperty("limit", out var limit)) Assert.Equal(limit.GetInt32(), query!.Limit);
            if (test.TryGetProperty("ageSeconds", out var age)) Assert.Equal(age.GetInt32(), (now.UtcDateTime - query!.SinceUtc).TotalSeconds);
            if (test.TryGetProperty("otpOnly", out var otp)) Assert.Equal(otp.GetBoolean(), query!.OtpOnly);
            if (test.TryGetProperty("keyword", out var keyword)) Assert.Equal(keyword.GetString(), query!.Keyword);
            if (test.TryGetProperty("sender", out var sender)) Assert.Equal(sender.GetString(), query!.Sender);
            if (test.TryGetProperty("slotId", out var slot)) Assert.Equal(slot.GetString(), query!.SlotId);
        }
    }
    [Fact]
    public void SharedSmsSegmentationVectorsMatchActualWindowsTpduPartsWithoutOversizedUserData()
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "sms-segmentation-v1.json")));
        foreach (var test in contract.RootElement.GetProperty("cases").EnumerateArray())
        {
            var text = string.Concat(Enumerable.Repeat(test.GetProperty("text").GetString(), test.GetProperty("repeat").GetInt32()));
            if (text.Length == 0) { Assert.Throws<ArgumentException>(() => SmsPdu.PrepareSubmitParts("+12345", text)); continue; }
            var gsm = test.GetProperty("encoding").GetString() == "gsm7";
            Assert.Equal(gsm, Gsm7Alphabet.CanEncode(text));
            Assert.Equal(test.GetProperty("units").GetInt32(), gsm ? Gsm7Alphabet.EncodeToSeptets(text).Length : text.Length);
            var parts = SmsPdu.PrepareSubmitParts("+12345", text, concatReference: 1);
            Assert.Equal(test.GetProperty("segments").GetInt32(), parts.Count);
            var decoded = new StringBuilder();
            foreach (var part in parts)
            {
                var offset = 4 + (part.Tpdu[2] + 1) / 2 + 2;
                var validity = (part.Tpdu[0] >> 3) & 3;
                offset += validity == 0 ? 0 : validity == 2 ? 1 : 7;
                var udl = part.Tpdu[offset++];
                Assert.InRange(part.Tpdu.Length - offset, 1, 140);
                var data = part.Tpdu[offset..]; var header = 0;
                if (parts.Count > 1)
                {
                    Assert.Equal(new byte[] { 5, 0, 3, 1, (byte)parts.Count, (byte)part.PartNumber }, data[..6]);
                    header = 6;
                }
                if (gsm)
                {
                    var headerSeptets = (header * 8 + 6) / 7;
                    decoded.Append(Gsm7Alphabet.DecodeSeptets(Gsm7Alphabet.UnpackSeptets(data, udl - headerSeptets, headerSeptets * 7)));
                }
                else
                {
                    Assert.Equal(data.Length, udl);
                    decoded.Append(Encoding.BigEndianUnicode.GetString(data[header..]));
                }
            }
            Assert.Equal(text, decoded.ToString());
        }
    }
    [Fact]
    public void SharedLinuxWindowsVectorsMatchBothExtractorAndPublicInboxModel()
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "otp-extraction-v1.json")));
        Assert.Equal(1, contract.RootElement.GetProperty("version").GetInt32());
        foreach (var test in contract.RootElement.GetProperty("cases").EnumerateArray())
        {
            var text = test.GetProperty("text").GetString()!;
            var expected = test.GetProperty("code").GetString();
            Assert.Equal(expected, SmsVerificationCodeExtractor.Extract(text));
            Assert.Equal(expected, new SmsMessageModel { Text = text }.ExtractedOtpCode);
        }
    }
}
