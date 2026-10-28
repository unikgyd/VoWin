using System.Text.Json;
using VoSharp.Kernel.SipGateway;

namespace VoSharp.Tests;

public class SipSmsOperationContractTests
{
    [Fact]
    public void SharedRustAndWindowsOperationIdentitiesIncludeZeroAndMaximumSipSequence()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "sip-sms-operation-v1.json")));
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
            Assert.Equal(item.GetProperty("operationId").GetString(), SipGateway.SmsOperationId(
                item.GetProperty("realm").GetString()!, item.GetProperty("account").GetString()!,
                item.GetProperty("callId").GetString()!, item.GetProperty("cseq").GetUInt32()));
    }
}
