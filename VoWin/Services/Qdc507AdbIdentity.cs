using System.IO;
using System.Security.Cryptography;

namespace VoWin.Services;

/// <summary>Pairs anonymous ADB transports to the selected Windows modem, never by list order.</summary>
internal static class Qdc507AdbIdentity
{
    internal const string RemoteHelper = "/tmp/vowin-call/vowin-qdc-identity.armv7";
    internal const string HelperSha256 = "64b16927e654d9ab98f61301fbede1a67a6231a7d122375d42ad9b4f43274c4d";
    private static readonly SemaphoreSlim DeploymentGate = new(1, 1);
    private const string HardwareGuard = "test \"$(uname -r)\" = 3.18.44 && " +
        "case \"$(cat /sys/devices/soc0/machine)\" in MDM9207|MDM9607) true;; *) false;; esac && test \"$(id -u)\" = 0";

    internal static bool IsImei(string? value) => value is { Length: 15 } && value.All(c => c is >= '0' and <= '9') &&
        value.Any(c => c != '0');

    internal static void RequireImei(string? value)
    {
        if (!IsImei(value)) throw new InvalidOperationException("尚未读到所选模组的有效 IMEI，无法安全配对 QDC507 ADB。请重新连接模组后重试。");
    }

    internal static bool IsVerifiedHardware(AdbShellResult result)
    {
        var lines = result.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return result.ExitCode == 0 && lines.Length == 3 && lines[0] == "3.18.44" &&
            lines[1] is "MDM9207" or "MDM9607" && lines[2] == "0";
    }

    internal static string? ParseImei(AdbShellResult result)
    {
        var lines = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var identities = lines.Where(line => line.StartsWith("VOWIN_IMEI=", StringComparison.Ordinal)).ToArray();
        return result.ExitCode == 0 && identities.Length == 1 && IsImei(identities[0][11..]) ? identities[0][11..] : null;
    }

    internal static Stream OpenHelper()
    {
        var stream = typeof(Qdc507AdbIdentity).Assembly.GetManifestResourceStream("VoWin.QdcIdentity.armv7")
            ?? throw new InvalidDataException("内置 QDC507 身份助手缺失，请使用完整发布版。");
        if (stream.Length != 3660 || !Convert.ToHexString(SHA256.HashData(stream)).Equals(HelperSha256, StringComparison.OrdinalIgnoreCase))
        {
            stream.Dispose();
            throw new InvalidDataException("内置 QDC507 身份助手校验失败。");
        }
        stream.Position = 0;
        return stream;
    }

    private static string HashCheck(string path) => $"printf '%s  %s\\n' {HelperSha256} {path} | sha256sum -c - >/dev/null 2>&1";

    internal static string GuardCommand(string expectedImei, string command)
    {
        RequireImei(expectedImei);
        // Identity and the actual command run on ONE remote shell. A server restart or
        // transport-ID reuse cannot retarget a route command after it was verified.
        return $"ulimit -c 0; if ! {{ {HardwareGuard} && {HashCheck(RemoteHelper)} && {RemoteHelper} --expect {expectedImei}; }}; " +
            "then printf 'QDC507 identity verification failed.\\n'; exit 72; fi;\n" + command;
    }

    internal static async Task<string?> ReadImeiAsync(AdbServerClient client, ulong transportId, CancellationToken ct)
    {
        var hardware = await client.ShellAsync(transportId, Qdc507VoiceRuntime.IdentityProbeCommand, ct).ConfigureAwait(false);
        if (!IsVerifiedHardware(hardware)) return null; // Phones/emulators never receive any helper.
        await DeploymentGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await client.ShellAsync(transportId, HardwareGuard + " && " + HashCheck(RemoteHelper), ct).ConfigureAwait(false);
            if (existing.ExitCode != 0)
            {
                var directory = await client.ShellAsync(transportId,
                    HardwareGuard + " && mkdir -p /tmp/vowin-call && chmod 700 /tmp/vowin-call", ct).ConfigureAwait(false);
                if (directory.ExitCode != 0) throw new IOException("QDC507 临时身份目录未就绪。");
                // Uploading this read-only helper cannot start a route or alter radio settings.
                // Atomic rename also avoids truncating an already running helper.
                var upload = RemoteHelper + ".upload-" + Guid.NewGuid().ToString("N");
                await using var helper = OpenHelper();
                await client.PushAsync(transportId, helper, upload, ct).ConfigureAwait(false);
                var install = await client.ShellAsync(transportId,
                    $"{HardwareGuard} && {HashCheck(upload)} && chmod 700 {upload} && mv -f {upload} {RemoteHelper}", ct).ConfigureAwait(false);
                if (install.ExitCode != 0) throw new IOException("QDC507 身份助手部署校验失败。");
            }
        }
        finally { DeploymentGate.Release(); }
        var identity = await client.ShellAsync(transportId, "ulimit -c 0; " + HardwareGuard + " && " +
            HashCheck(RemoteHelper) + " && " + RemoteHelper, ct).ConfigureAwait(false);
        return ParseImei(identity);
    }

    internal static async Task<ulong> SelectTransportAsync(IEnumerable<AdbTransportDevice> devices, string expectedImei,
        Func<ulong, CancellationToken, Task<string?>> readImei, CancellationToken ct)
    {
        RequireImei(expectedImei);
        var matches = new List<ulong>();
        foreach (var device in devices)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(8));
                if (await readImei(device.TransportId, deadline.Token).ConfigureAwait(false) == expectedImei) matches.Add(device.TransportId);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (IOException) { }
        }
        return matches.Count == 1 ? matches[0] : throw new InvalidOperationException(
            $"未找到唯一匹配所选模组 IMEI 的 QDC507 ADB 通道（匹配数 {matches.Count}）；未修改语音路由。");
    }
}
