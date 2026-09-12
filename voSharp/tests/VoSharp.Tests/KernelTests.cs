using VoSharp.Common.Events;
using VoSharp.Kernel;
using VoSharp.Kernel.Ipc;
using VoSharp.StateMachine;
using VoSharp.Telephony.VoWifi;
using Xunit;

namespace VoSharp.Tests;

public class KernelTests
{
    [Fact]
    public async Task TestKernel_CommandExecution_Lifecycle()
    {
        await using var kernel = new VoKernel();

        // 1. Initial status
        var statusRes = await kernel.ExecuteCommandAsync("status");
        Assert.True(statusRes.Success);

        // 2. MMI execution
        var mmiRes = await kernel.ExecuteCommandAsync("mmi *#06#");
        Assert.True(mmiRes.Success);
        Assert.Contains("IMEI", mmiRes.Message);

        // 3. AKA evaluation
        var akaRes = await kernel.ExecuteCommandAsync("aka 23553cbe9637a89d218ae64dae47bf35 55f328b43577b9b94a9ffac354dfafb3");
        Assert.True(akaRes.Success);

        // 4. Offline call operations fail closed and do not create state
        var dialRes = await kernel.ExecuteCommandAsync("dial 10086");
        Assert.False(dialRes.Success);
        Assert.Null(kernel.ActiveCall);

        var ansRes = await kernel.ExecuteCommandAsync("answer");
        Assert.False(ansRes.Success);
        Assert.Null(kernel.ActiveCall);

        var hangupRes = await kernel.ExecuteCommandAsync("hangup");
        Assert.False(hangupRes.Success);
        Assert.Null(kernel.ActiveCall);

        // 5. Offline SMS fails closed and is not written to the outbox
        var smsRes = await kernel.ExecuteCommandAsync("sms send 10086 CXCX");
        Assert.False(smsRes.Success);
        Assert.Empty(kernel.Outbox);

        // 6. History
        var histRes = await kernel.ExecuteCommandAsync("history");
        Assert.True(histRes.Success);
    }

    [Fact]
    public async Task TestKernel_EmergencyAndSupplementaryCommands()
    {
        await using var kernel = new VoKernel();

        // Emergency dialing must never report success without a real emergency bearer
        var dialRes = await kernel.ExecuteCommandAsync("dial 110");
        Assert.False(dialRes.Success);
        Assert.Contains("not implemented", dialRes.Message);
        Assert.Null(kernel.ActiveCall);

        // Signal query (simulated)
        var sigRes = await kernel.ExecuteCommandAsync("signal");
        Assert.True(sigRes.Success);

        // Never fabricate a SIM identity when no modem is attached.
        var simRes = await kernel.ExecuteCommandAsync("sim");
        Assert.False(simRes.Success);
        Assert.Contains("No physical SIM", simRes.Message);

        // Reset
        var resetRes = await kernel.ExecuteCommandAsync("reset");
        Assert.True(resetRes.Success);
    }

    [Fact]
    public async Task TestKernel_Roaming_And_VoWifi_Commands()
    {
        await using var kernel = new VoKernel();

        // 1. VoWiFi Status
        var voStat = await kernel.ExecuteCommandAsync("vowifi status");
        Assert.True(voStat.Success);
        Assert.Contains("Disconnected", voStat.Message);

        // 2. VoWiFi Info
        var voInfo = await kernel.ExecuteCommandAsync("vowifi info");
        // No SIM attached yet, should report gracefully
        Assert.False(voInfo.Success);

        // 3. VoWiFi Stop
        var voStop = await kernel.ExecuteCommandAsync("vowifi stop");
        Assert.True(voStop.Success);

        // 4. Roaming query (no modem attached)
        var roRes = await kernel.ExecuteCommandAsync("roaming");
        Assert.False(roRes.Success);
    }

    [Theory]
    [InlineData("aka 00 00")]
    [InlineData("at AT+CSQ")]
    [InlineData("raw AT+CSQ")]
    [InlineData("euicc delete 8901")]
    public void NamedPipeServer_BlocksSensitiveCommands(string command)
    {
        Assert.False(NamedPipeServer.IsCommandAllowed(command));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("sms inbox")]
    [InlineData("call status")]
    [InlineData("euicc list")]
    public void NamedPipeServer_AllowsNonSensitiveCommands(string command)
    {
        Assert.True(NamedPipeServer.IsCommandAllowed(command));
    }

    [Fact]
    public async Task NamedPipeServer_RejectsNulAndOversizedCommands()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NamedPipeServer.ReadCommandAsync(new StringReader("status\0\n"), CancellationToken.None));

        var oversized = new string('x', 65537) + "\n";
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NamedPipeServer.ReadCommandAsync(new StringReader(oversized), CancellationToken.None));
    }

    [Fact]
    public async Task AkaCommand_DoesNotReturnKeyMaterial()
    {
        await using var kernel = new VoKernel();

        var result = await kernel.ExecuteCommandAsync(
            "aka 23553cbe9637a89d218ae64dae47bf35 55f328b43577b9b94a9ffac354dfafb3 --software");
        var json = result.ToJson();

        Assert.True(result.Success);
        Assert.DoesNotContain("\"RES\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"CK\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"IK\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ResLength", json);
    }

    [Fact]
    public async Task TestKernel_StronglyTyped_GuiApis()
    {
        await using var kernel = new VoKernel();

        // 1. Snapshot creation
        var snapshot = kernel.CreateSnapshot();
        Assert.NotNull(snapshot);
        Assert.Equal(TelephonyState.Init, snapshot.State);
        Assert.Equal(0, snapshot.InboxCount);
        Assert.Equal(0, snapshot.OutboxCount);

        // 2. Inbox / Outbox methods
        Assert.NotNull(kernel.GetInbox());
        Assert.NotNull(kernel.GetOutbox());
        kernel.ClearInbox();
        Assert.False(kernel.DeleteInboxMessage(999));

        // 3. VoWiFi methods
        var voDiag = kernel.GetVoWifiDiagnostics();
        Assert.NotNull(voDiag);
        Assert.Equal(VoWifiState.Disconnected, voDiag.State);
        var stopRes = await kernel.StopVoWifiAsync();
        Assert.True(stopRes);

        // 4. Slots & Pool methods
        var slots = kernel.GetSlots();
        Assert.NotNull(slots);
        Assert.Null(kernel.GetActiveSlot());
        Assert.False(kernel.SelectSlot("non_existent"));
        Assert.False(kernel.SetSlotProxy("non_existent", "socks5://127.0.0.1:1080"));

        // 5. Offline Calls throw or fail closed
        await Assert.ThrowsAsync<NotSupportedException>(() => kernel.DialAsync("110"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kernel.DialAsync("10086"));
        Assert.Null(await kernel.HangupAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => kernel.AnswerAsync());
        Assert.Null(await kernel.RejectAsync());
        Assert.False(await kernel.SendDtmfAsync('1'));

        // 6. Offline SMS fails closed
        await Assert.ThrowsAsync<InvalidOperationException>(() => kernel.SendSmsAsync("10086", "Hello"));

        // 7. Refresh metrics (offline returns null)
        Assert.Null(await kernel.RefreshSignalAsync());
        Assert.Null(await kernel.RefreshRegistrationAsync());
        Assert.Null(await kernel.RefreshSimAsync());
    }

    private static string CallSessionState(VoKernel kernel)
    {
        return kernel.ActiveCall?.State.ToString() ?? "";
    }
}
