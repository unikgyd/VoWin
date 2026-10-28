using System.Text.Json;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public class ImsSmsRelayContractTests
{
    [Fact]
    public void SharedRelayFramesRejectMalformedAcknowledgements()
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "contracts", "ims-sms-rpdu-v1.json")));
        foreach (var item in contract.RootElement.GetProperty("parse").EnumerateArray())
        {
            var data = Convert.FromHexString(item.GetProperty("hex").GetString()!);
            var accepted = item.GetProperty("accepted").GetBoolean();
            RpMessage? parsed = null;
            var error = Record.Exception(() => parsed = ImsSmsHandler.ParseRpdu(data));
            Assert.True((error is null) == accepted, item.GetProperty("id").GetString());
            if (!accepted) continue;
            Assert.Equal(item.GetProperty("type").GetByte(), parsed!.MessageType);
            Assert.Equal(item.GetProperty("reference").GetByte(), parsed.Reference);
            var tpdu = item.GetProperty("tpdu").GetString();
            Assert.Equal(tpdu, parsed.Tpdu is null ? null : Convert.ToHexString(parsed.Tpdu).ToLowerInvariant());
            var cause = item.GetProperty("cause");
            Assert.Equal(cause.ValueKind == JsonValueKind.Null ? (byte?)null : cause.GetByte(), parsed.Cause);
        }
        foreach (var item in contract.RootElement.GetProperty("build").EnumerateArray())
        {
            var data = ImsSmsHandler.BuildRpData(item.GetProperty("reference").GetByte(),
                item.GetProperty("smsc").GetString()!, Convert.FromHexString(item.GetProperty("tpdu").GetString()!));
            Assert.Equal(item.GetProperty("expected").GetString(), Convert.ToHexString(data).ToLowerInvariant());
        }
    }

    [Fact]
    public void RelayEnvelopeBoundsAndNoPrefixOrSuffixAcceptance()
    {
        var data = ImsSmsHandler.BuildRpData(255, "+12345678901234567890", new byte[232]);
        Assert.Equal(232, ImsSmsHandler.ParseRpdu(data).Tpdu!.Length);
        for (int end = 0; end < data.Length; end++)
            Assert.Throws<ArgumentException>(() => ImsSmsHandler.ParseRpdu(data[..end]));
        Assert.Throws<ArgumentException>(() => ImsSmsHandler.ParseRpdu([..data, 0]));
        foreach (var value in new[] { "", "+", "++1", "１２３", "123456789012345678901", "1\r\n2" })
            Assert.Throws<ArgumentException>(() => ImsSmsHandler.EncodeRpAddress(value));
    }
}
