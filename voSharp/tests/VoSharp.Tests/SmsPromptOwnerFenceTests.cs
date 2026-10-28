using VoSharp.Modem.At;

namespace VoSharp.Tests;

public sealed class SmsPromptOwnerFenceTests
{
    [Fact]
    public void IdentityChangeWhileWaitingForPromptCannotWritePayloadOrControlZ()
    {
        var writes = new List<string>();
        Assert.Throws<InvalidOperationException>(() => AtSession.WritePromptPayload("001122", default,
            () => throw new InvalidOperationException("Original SMS owner is no longer valid."), writes.Add));
        Assert.Empty(writes);
    }

    [Fact]
    public void OwnerCancellationDuringFinalGuardCannotWritePayloadOrControlZ()
    {
        using var owner = new CancellationTokenSource(); var writes = new List<string>();
        Assert.ThrowsAny<OperationCanceledException>(() => AtSession.WritePromptPayload("001122", owner.Token, owner.Cancel, writes.Add));
        Assert.Empty(writes);
    }

    [Fact]
    public void AlreadyCancelledPromptDoesNotInvokeOwnerOrWrite()
    {
        using var owner = new CancellationTokenSource(); owner.Cancel();
        var guarded = false; var writes = new List<string>();
        Assert.ThrowsAny<OperationCanceledException>(() => AtSession.WritePromptPayload("001122", owner.Token,
            () => guarded = true, writes.Add));
        Assert.False(guarded); Assert.Empty(writes);
    }

    [Fact]
    public void ValidOriginalOwnerWritesExactlyOneCompletePayload()
    {
        var guarded = false; var writes = new List<string>();
        AtSession.WritePromptPayload("001122", default, () => guarded = true, value => { Assert.True(guarded); writes.Add(value); });
        Assert.Equal(["001122\x1A"], writes);
    }
}
