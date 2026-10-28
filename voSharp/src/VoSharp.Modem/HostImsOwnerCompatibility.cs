using System.Diagnostics;
using System.Text.Json;
using VoSharp.Common.Utils;

namespace VoSharp.Modem;

public static class HostImsOwnerCompatibility
{
    private const string IncompatibleMessage =
        "Host IMS 承载组件版本不兼容，请使用配套完整发布版；源码调试请先生成 packaging/runtime.zip 并重新编译。未修改模组。";

    internal static HostImsOwnerProtocol Parse(string json)
    {
        try
        {
            if (json.Length > 8192) throw new JsonException();
            var protocol = JsonSerializer.Deserialize<HostImsOwnerProtocol>(json);
            if (protocol?.IsCompatible != true) throw new JsonException();
            return protocol;
        }
        catch (JsonException ex) { throw new InvalidOperationException(IncompatibleMessage, ex); }
    }

    /// <summary>Only asks the owner to describe its command protocol; never starts a bearer.</summary>
    public static async Task VerifyAsync(string ownerPath, CancellationToken ct = default)
    {
        var start = new ProcessStartInfo(Path.GetFullPath(ownerPath))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--describe-protocol");
        await VerifyProcessAsync(start, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
    }

    internal static async Task VerifyProcessAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cancellation.CancelAfter(timeout);
        using var process = new Process { StartInfo = start };
        Task<string>? output = null, error = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!process.Start()) throw new IOException("Could not start IMS owner protocol check.");
            output = ReadBoundedAsync(process.StandardOutput, cancellation.Token);
            error = ReadBoundedAsync(process.StandardError, cancellation.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(cancellation.Token)).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidDataException("IMS owner rejected the read-only protocol check.");
            _ = Parse(await output.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(IncompatibleMessage, ex);
        }
        finally
        {
            try { if (process.Id != 0 && !process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            cancellation.Cancel();
            // Observe both redirected readers even on timeout/cancellation.
            foreach (var reader in new[] { output, error })
                if (reader is not null) { try { await reader.ConfigureAwait(false); } catch { } }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var result = new System.Text.StringBuilder();
        var buffer = new char[1024];
        while (await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false) is var length && length != 0)
        {
            if (result.Length + length > 8192) throw new InvalidDataException("IMS owner protocol response too large.");
            result.Append(buffer, 0, length);
        }
        return result.ToString();
    }
}
