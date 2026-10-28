using System.Text;
using System.Runtime.Versioning;
using VoSharp.HostIms;

namespace VoSharp.Tests;

[SupportedOSPlatform("windows")]
public sealed class ParentPipeWriterTests
{
    [Fact]
    public async Task SyncAndAsyncProtocolOutputIsPreserved()
    {
        using var inner = new StringWriter();
        using var writer = new ParentPipeWriter(inner);
        writer.Write('A'); writer.Write("BC"); writer.WriteLine("D");
        await writer.WriteLineAsync("READY");
        await writer.FlushAsync();
        Assert.Equal("ABCD" + Environment.NewLine + "READY" + Environment.NewLine, inner.ToString());
    }

    private sealed class BrokenWriter(Exception failure) : TextWriter
    {
        public int Attempts;
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) { Attempts++; throw failure; }
        public override void Write(string? value) { Attempts++; throw failure; }
        public override void WriteLine(string? value) { Attempts++; throw failure; }
        public override void Flush() { Attempts++; throw failure; }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ParentPipeLossCannotInterruptIndependentCleanup(bool disposed)
    {
        using var inner = new BrokenWriter(disposed ? new ObjectDisposedException("parent pipe") : new IOException("parent gone"));
        using var writer = new ParentPipeWriter(inner);
        await writer.WriteLineAsync("BEARER_LOST");
        writer.WriteLine("restoring"); writer.Write('x'); writer.Write("done");
        await writer.FlushAsync();
        Assert.Equal(1, inner.Attempts);
    }

    [Fact]
    public void UnrelatedProgrammingFailuresAreNotHidden()
    {
        using var inner = new BrokenWriter(new InvalidOperationException("bug"));
        using var writer = new ParentPipeWriter(inner);
        Assert.Throws<InvalidOperationException>(() => writer.WriteLine("test"));
    }
}
