using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VoWin.Services;

internal sealed record AdbShellResult(int ExitCode, string Output);
internal sealed record AdbTransportDevice(string Serial, ulong TransportId);

/// <summary>
/// Managed smart-socket and sync-v1 client. The existing ADB server owns USB/authentication;
/// commands never start a CLI process, restart the shared server or select an arbitrary device.
/// Protocol: AOSP adb OVERVIEW.TXT, SERVICES.TXT and SYNC.TXT.
/// </summary>
internal sealed class AdbServerClient(int port = 5037)
{
    private const int MaxOutputBytes = 2 * 1024 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    private async Task<TcpClient> ConnectAsync(CancellationToken ct)
    {
        var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try { await client.ConnectAsync(IPAddress.Loopback, port, ct).ConfigureAwait(false); return client; }
        catch { client.Dispose(); throw; }
    }

    public Task<string> DevicesAsync(CancellationToken ct) => DeviceListAsync("host:devices", ct);
    public Task<string> DevicesLongAsync(CancellationToken ct) => DeviceListAsync("host:devices-l", ct);

    private async Task<string> DeviceListAsync(string service, CancellationToken ct)
    {
        using var client = await ConnectAsync(ct).ConfigureAwait(false);
        var stream = client.GetStream();
        await RequestAsync(stream, service, ct).ConfigureAwait(false);
        return await ReadStringAsync(stream, ct).ConfigureAwait(false);
    }

    private static string SerialSelector(string serial)
    {
        if (string.IsNullOrWhiteSpace(serial) || serial.Length > 512 || serial.Any(char.IsControl))
            throw new ArgumentException("A specific ADB serial is required.", nameof(serial));
        return "host:transport:" + serial;
    }

    private static string IdSelector(ulong transportId) => transportId != 0
        ? "host:transport-id:" + transportId.ToString(CultureInfo.InvariantCulture)
        : throw new ArgumentOutOfRangeException(nameof(transportId), "A nonzero ADB transport ID is required.");

    internal static AdbTransportDevice[] ParseDeviceTransports(string inventory, string? configuredSerial = null)
    {
        var result = new List<AdbTransportDevice>();
        var ids = new HashSet<ulong>();
        foreach (var line in inventory.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || fields[1] != "device" || fields[0].Contains(':') ||
                fields[0].StartsWith("emulator-", StringComparison.Ordinal) ||
                !string.IsNullOrWhiteSpace(configuredSerial) && fields[0] != configuredSerial.Trim()) continue;
            var transport = fields.Skip(2).Where(field => field.StartsWith("transport_id:", StringComparison.Ordinal)).ToArray();
            if (transport.Length != 1 || !ulong.TryParse(transport[0].AsSpan(13), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var id) || id == 0 || !ids.Add(id))
                throw new IOException("ADB inventory has missing or ambiguous transport IDs; device selection was refused.");
            result.Add(new(fields[0], id));
        }
        return result.ToArray();
    }

    private async Task<TcpClient> TransportAsync(string selector, string service, CancellationToken ct)
    {
        var client = await ConnectAsync(ct).ConfigureAwait(false);
        try
        {
            await RequestAsync(client.GetStream(), selector, ct).ConfigureAwait(false);
            await RequestAsync(client.GetStream(), service, ct).ConfigureAwait(false);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    public Task<AdbShellResult> ShellAsync(string serial, string command, CancellationToken ct) =>
        ShellCoreAsync(SerialSelector(serial), command, ct);
    public Task<AdbShellResult> ShellAsync(ulong transportId, string command, CancellationToken ct) =>
        ShellCoreAsync(IdSelector(transportId), command, ct);

    private async Task<AdbShellResult> ShellCoreAsync(string selector, string command, CancellationToken ct)
    {
        // The QDC507 daemon predates shell-v2. Capture a real remote exit status with a
        // per-request marker, even for commands containing `exit`, quotes or newlines.
        var marker = "__VOWIN_EXIT_" + Guid.NewGuid().ToString("N") + "__";
        var quoted = "'" + command.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
        var service = "shell:sh -c " + quoted + " </dev/null; printf '\\n" + marker + ":%s\\n' \"$?\"";
        using var client = await TransportAsync(selector, service, ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (await client.GetStream().ReadAsync(buffer, ct).ConfigureAwait(false) is var read && read != 0)
        {
            if (output.Length + read > MaxOutputBytes) throw new InvalidDataException("ADB shell output exceeds the diagnostic limit.");
            output.Write(buffer, 0, read);
        }
        return ParseShellResult(Utf8.GetString(output.GetBuffer(), 0, checked((int)output.Length)), marker);
    }

    internal static AdbShellResult ParseShellResult(string output, string marker)
    {
        var index = output.LastIndexOf("\n" + marker + ":", StringComparison.Ordinal);
        if (index < 0 || !int.TryParse(output.AsSpan(index + marker.Length + 2).Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out var exitCode) || exitCode is < 0 or > 255)
            throw new IOException("ADB shell closed without a valid remote exit status.");
        return new(exitCode, output[..index]);
    }

    public Task PushAsync(string serial, Stream content, string remote, CancellationToken ct) =>
        PushCoreAsync(SerialSelector(serial), content, remote, ct);
    public Task PushAsync(ulong transportId, Stream content, string remote, CancellationToken ct) =>
        PushCoreAsync(IdSelector(transportId), content, remote, ct);

    private async Task PushCoreAsync(string selector, Stream content, string remote, CancellationToken ct)
    {
        // This client is only for the known modem runtime; no arbitrary/block-device upload.
        if (!remote.StartsWith("/tmp/vowin-call/", StringComparison.Ordinal) || remote.Contains("..", StringComparison.Ordinal) ||
            remote.Contains(',') || remote.Any(char.IsControl) || remote.EndsWith('/'))
            throw new ArgumentException("Invalid voice runtime destination.", nameof(remote));
        var filename = Utf8.GetBytes(remote + ",33188"); // regular file, 0644; scripts chmod explicitly after verification.
        if (filename.Length > 1024) throw new ArgumentException("ADB destination exceeds the sync-v1 limit.");
        using var client = await TransportAsync(selector, "sync:", ct).ConfigureAwait(false);
        var stream = client.GetStream();
        await SyncHeaderAsync(stream, "SEND", (uint)filename.Length, ct).ConfigureAwait(false);
        await stream.WriteAsync(filename, ct).ConfigureAwait(false);
        var buffer = new byte[64 * 1024];
        while (await content.ReadAsync(buffer, ct).ConfigureAwait(false) is var read && read != 0)
        {
            await SyncHeaderAsync(stream, "DATA", (uint)read, ct).ConfigureAwait(false);
            await stream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        await SyncHeaderAsync(stream, "DONE", checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ct).ConfigureAwait(false);
        var reply = new byte[8];
        await stream.ReadExactlyAsync(reply, ct).ConfigureAwait(false);
        var status = Encoding.ASCII.GetString(reply, 0, 4);
        if (status == "OKAY") return;
        if (status != "FAIL") throw new InvalidDataException("Invalid ADB sync response.");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(4));
        if (length > 65536) throw new InvalidDataException("Invalid ADB sync error size.");
        var error = new byte[length];
        await stream.ReadExactlyAsync(error, ct).ConfigureAwait(false);
        throw new IOException("ADB upload failed: " + Utf8.GetString(error));
    }

    private static async Task SyncHeaderAsync(Stream stream, string id, uint value, CancellationToken ct)
    {
        var header = new byte[8];
        Encoding.ASCII.GetBytes(id, header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), value);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
    }

    private static async Task RequestAsync(Stream stream, string service, CancellationToken ct)
    {
        var data = Utf8.GetBytes(service);
        if (data.Length > ushort.MaxValue) throw new ArgumentException("ADB request exceeds the smart-socket limit.");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(data.Length.ToString("X4", CultureInfo.InvariantCulture)), ct).ConfigureAwait(false);
        await stream.WriteAsync(data, ct).ConfigureAwait(false);
        var status = new byte[4];
        await stream.ReadExactlyAsync(status, ct).ConfigureAwait(false);
        if (status.AsSpan().SequenceEqual("OKAY"u8)) return;
        if (status.AsSpan().SequenceEqual("FAIL"u8)) throw new IOException("ADB: " + await ReadStringAsync(stream, ct).ConfigureAwait(false));
        throw new InvalidDataException("Invalid ADB smart-socket response.");
    }

    private static async Task<string> ReadStringAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        if (!ushort.TryParse(Encoding.ASCII.GetString(header), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size))
            throw new InvalidDataException("Invalid ADB string length.");
        var bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return Utf8.GetString(bytes);
    }
}
