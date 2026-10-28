using VoSharp.Sim;

namespace VoSharp.Tests;

public class AkaExchangeCompatibilityTests
{
    private static byte[] Command() => HardwareAka.BuildAuthenticateApdu(new byte[16], new byte[16]);
    private static byte[] Body() => [0xDB, 4, 0x11, 0x11, 0x11, 0x11, 16,
        .. Enumerable.Repeat((byte)0x22, 16), 16, .. Enumerable.Repeat((byte)0x33, 16)];

    private static Task<byte[]?> Exchange(bool asynchronous, byte[] command, Func<byte[], byte[]?> transmit) =>
        asynchronous
            ? HardwareAka.TransmitWithChainingAsync((bytes, _) => Task.FromResult(transmit(bytes)), command, 2)
            : Task.FromResult(HardwareAka.TransmitWithChaining(transmit, command, 2));

    [Theory]
    [InlineData(false, 0x61)]
    [InlineData(true, 0x61)]
    [InlineData(false, 0x9F)]
    [InlineData(true, 0x9F)]
    public async Task ContinuationsPreserveEveryBodyByteAndUseTheSelectedChannel(bool asynchronous, byte status)
    {
        var body = Body();
        var calls = new List<byte[]>();
        var raw = await Exchange(asynchronous, Command(), command =>
        {
            calls.Add(command.ToArray());
            return calls.Count == 1
                ? [.. body.AsSpan(0, 7), status, (byte)(body.Length - 7)]
                : [.. body.AsSpan(7), 0x90, 0];
        });

        Assert.Equal(2, calls.Count);
        Assert.Equal(new byte[] { 2, 0xC0, 0, 0, (byte)(body.Length - 7) }, calls[1]);
        Assert.Equal((byte[])[.. body, 0x90, 0], raw);
        Assert.True(HardwareAka.ParseAuthenticateResponse(raw!).Success);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task WrongLeCorrectionPreservesTheCommandAndAlreadyCollectedBody(bool asynchronous, bool duringGetResponse)
    {
        var body = Body();
        var original = Command();
        var snapshot = original.ToArray();
        var calls = new List<byte[]>();
        var raw = await Exchange(asynchronous, original, command =>
        {
            calls.Add(command.ToArray());
            if (duringGetResponse && calls.Count == 1) return [.. body.AsSpan(0, 7), 0x61, 0];
            if (calls.Count == (duringGetResponse ? 2 : 1)) return [0x6C, (byte)(body.Length - (duringGetResponse ? 7 : 0))];
            return [.. body.AsSpan(duringGetResponse ? 7 : 0), 0x90, 0];
        });

        Assert.Equal(snapshot, original);
        Assert.Equal(duringGetResponse ? 3 : 2, calls.Count);
        Assert.Equal(calls[^2].AsSpan(0, calls[^2].Length - 1).ToArray(), calls[^1].AsSpan(0, calls[^1].Length - 1).ToArray());
        Assert.Equal(body.Length - (duringGetResponse ? 7 : 0), calls[^1][^1]);
        Assert.Equal((byte[])[.. body, 0x90, 0], raw);
        Assert.True(HardwareAka.ParseAuthenticateResponse(raw!).Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongLeDoesNotOverwriteDataOnlyOrExtendedApdus(bool asynchronous)
    {
        foreach (var command in new byte[][]
        {
            [2, 0xA4, 0, 4, 2, 0x6F, 2], // Case 3: last byte is file ID, not Le.
            [2, 0xB0, 0, 0, 0, 1, 0],     // Extended Case 2: deliberately unsupported.
            [2, 0xB0, 0, 0],
        })
        {
            var snapshot = command.ToArray();
            var calls = 0;
            var raw = await Exchange(asynchronous, command, _ => { calls++; return [0x6C, 0x10]; });
            Assert.Equal(1, calls);
            Assert.Equal(snapshot, command);
            Assert.Equal(new byte[] { 0x6C, 0x10 }, raw);
        }
    }

    [Theory]
    [InlineData(false, 0x6C)]
    [InlineData(true, 0x6C)]
    [InlineData(false, 0x61)]
    [InlineData(true, 0x61)]
    [InlineData(false, 0x9F)]
    [InlineData(true, 0x9F)]
    public async Task ContinuationLoopsRemainBounded(bool asynchronous, byte status)
    {
        var calls = 0;
        var raw = await Exchange(asynchronous, Command(), _ => { calls++; return [status, 0x10]; });
        Assert.Null(raw);
        Assert.Equal(HardwareAka.MaxChainedExchanges, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedWrongLeAndExcessiveBodiesAreNotRetried(bool asynchronous)
    {
        var calls = 0;
        Assert.Null(await Exchange(asynchronous, Command(), _ => { calls++; return [1, 0x6C, 0x10]; }));
        Assert.Equal(1, calls);
        calls = 0;
        Assert.Null(await Exchange(asynchronous, Command(), _ =>
        {
            calls++;
            return [.. new byte[2049], 0x61, 0];
        }));
        Assert.Equal(2, calls);
        Assert.Equal(4098, (await Exchange(asynchronous, Command(), _ => [.. new byte[4096], 0x90, 0]))!.Length);
        Assert.Null(await Exchange(asynchronous, Command(), _ => [.. new byte[4097], 0x90, 0]));
    }

    [Fact]
    public async Task AlreadyCancelledExchangeDoesNotTouchTransport()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HardwareAka.TransmitWithChainingAsync(
            (_, _) => { calls++; return Task.FromResult<byte[]?>([0x90, 0]); }, Command(), 2, cancellation.Token));
        Assert.Equal(0, calls);
    }
}
