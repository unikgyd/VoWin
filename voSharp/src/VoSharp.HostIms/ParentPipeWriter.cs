using System.Text;

namespace VoSharp.HostIms;

/// <summary>Parent GUI exit must not abort RAS/route cleanup or independent modem restoration.</summary>
internal sealed class ParentPipeWriter(TextWriter inner) : TextWriter
{
    public override Encoding Encoding => inner.Encoding;
    private int _disconnected;
    public override void Write(char value) => Try(() => inner.Write(value));
    public override void Write(string? value) => Try(() => inner.Write(value));
    public override void WriteLine(string? value) => Try(() => inner.WriteLine(value));
    public override void Flush() => Try(inner.Flush);
    private void Try(Action write)
    {
        if (Volatile.Read(ref _disconnected) != 0) return;
        try { write(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        { Volatile.Write(ref _disconnected, 1); }
    }
}
