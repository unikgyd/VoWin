using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace VoWin.Services;

/// <summary>
/// Restores the USB voice route removed from the Baiwang QDC507 firmware.
/// The runtime executes in the modem's Linux system; Windows only deploys it
/// through the already authorized root ADB interface.
/// </summary>
internal sealed class Qdc507VoiceRuntime
{
    private const string RuntimeVersion = "qdc507-3.18.44-voice-20260712.5";
    private const string Commit = "0443dfdaf8aec086fd76ba2ee9152fd908114524";
    private const string RemoteDirectory = "/tmp/vowin-call";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _prepared;

    private static readonly RuntimeFile[] Files =
    [
        new("qdc507_aprv3.ko", 36664, "3d82d3dec4f1e323201bba87156df9d41438e08314097353f2607f9117211d4a"),
        new("qdc507_voice.ko", 999236, "ed3821682d5309969a01c764192c83feff9669c61ef237c69475cd1619cf296c"),
        new("mavo-pcm-bridge.armv7", 17860, "88d47c15e61d1428a59c821fed804c2e6490e82859a085062f21966b58d167fc")
    ];

    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_prepared && await IsPreparedAsync(cancellationToken).ConfigureAwait(false)) return;

            var uid = await AdbShellAsync("id -u", TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
            if (!uid.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("0"))
                throw new InvalidOperationException("QDC507 ADB is connected but does not provide a root shell.");

            var kernel = (await AdbShellAsync("uname -r", TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false)).Trim();
            if (!string.Equals(kernel, "3.18.44", StringComparison.Ordinal))
                throw new InvalidOperationException($"QDC507 voice runtime requires kernel 3.18.44; modem reports {kernel}.");

            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VoWin", "voice-runtime", RuntimeVersion);
            Directory.CreateDirectory(directory);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            foreach (var file in Files)
            {
                var localPath = Path.Combine(directory, file.Name);
                if (!await VerifyAsync(localPath, file, cancellationToken).ConfigureAwait(false))
                {
                    var url = $"https://raw.githubusercontent.com/moluncn/mavo/{Commit}/Resources/ModuleVoice/{file.Name}";
                    var bytes = await http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
                    await File.WriteAllBytesAsync(localPath, bytes, cancellationToken).ConfigureAwait(false);
                    if (!await VerifyAsync(localPath, file, cancellationToken).ConfigureAwait(false))
                        throw new InvalidDataException($"Downloaded QDC507 runtime file failed verification: {file.Name}");
                }
            }

            await AdbShellAsync($"mkdir -p {RemoteDirectory} && chmod 700 {RemoteDirectory}", TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
            foreach (var file in Files)
            {
                await RunAdbAsync(["push", Path.Combine(directory, file.Name), $"{RemoteDirectory}/{file.Name}"], TimeSpan.FromSeconds(35), cancellationToken).ConfigureAwait(false);
            }

            await PushScriptAsync("calibrate.sh", CalibrationScript, cancellationToken).ConfigureAwait(false);
            await PushScriptAsync("start-route.sh", StartRouteScript, cancellationToken).ConfigureAwait(false);
            await PushScriptAsync("stop-route.sh", StopRouteScript, cancellationToken).ConfigureAwait(false);

            var prepare = $"chmod 755 {RemoteDirectory}/mavo-pcm-bridge.armv7 {RemoteDirectory}/*.sh; " +
                          $"grep -q '^qdc507_aprv3 ' /proc/modules || insmod {RemoteDirectory}/qdc507_aprv3.ko; " +
                          $"grep -q '^qdc507_voice ' /proc/modules || insmod {RemoteDirectory}/qdc507_voice.ko";
            await AdbShellAsync(prepare, TimeSpan.FromSeconds(25), cancellationToken).ConfigureAwait(false);

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!await SoundDevicesReadyAsync(cancellationToken).ConfigureAwait(false))
            {
                if (DateTime.UtcNow >= deadline)
                    throw new InvalidOperationException("QDC507 drivers loaded, but the D4/D5/D6 ALSA devices did not appear.");
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }

            await AdbShellAsync($"{RemoteDirectory}/calibrate.sh", TimeSpan.FromSeconds(25), cancellationToken).ConfigureAwait(false);
            await AdbShellAsync($"{RemoteDirectory}/mavo-pcm-bridge.armv7 --check", TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            _prepared = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StartRouteAsync(CancellationToken cancellationToken = default)
    {
        await PrepareAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Do not reuse an apparently-running D4 route. QDC507 can leave a
            // stale UAC stream after the previous call; it reports RUNNING but
            // only returns zero PCM. Start every cellular call from a fully
            // detached USB-audio function, matching the proven CLI sequence.
            await TryAdbShellAsync($"test ! -x {RemoteDirectory}/stop-route.sh || {RemoteDirectory}/stop-route.sh", cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            // audio_enable=1 may re-enumerate the USB composite device and cut
            // off the shell that launched the helper. Poll the resulting state
            // even when that launch shell reports a transport error.
            try
            {
                await AdbShellAsync($"{RemoteDirectory}/start-route.sh", TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
            catch { }
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!await RouteReadyAsync(cancellationToken).ConfigureAwait(false))
            {
                if (DateTime.UtcNow >= deadline)
                {
                    var log = await TryAdbShellAsync("tail -n 80 /run/vowin-voice-route.log", cancellationToken).ConfigureAwait(false);
                    throw new InvalidOperationException("QDC507 D4/UAC route did not enter RUNNING. " + log.Trim());
                }
                await Task.Delay(300, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopRouteAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await TryAdbShellAsync($"test ! -x {RemoteDirectory}/stop-route.sh || {RemoteDirectory}/stop-route.sh", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> IsPreparedAsync(CancellationToken ct) =>
        await SoundDevicesReadyAsync(ct).ConfigureAwait(false) &&
        (await TryAdbShellAsync($"{RemoteDirectory}/mavo-pcm-bridge.armv7 --check", ct).ConfigureAwait(false))
            .Contains("required symbols", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> SoundDevicesReadyAsync(CancellationToken ct)
    {
        const string check = "test -c /dev/snd/controlC0 && test -c /dev/snd/pcmC0D4p && test -c /dev/snd/pcmC0D4c && " +
                             "test -c /dev/snd/pcmC0D5p && test -c /dev/snd/pcmC0D6c && grep -Fq mdm9607-tomtom-i2s-snd-card /proc/asound/cards";
        return await AdbShellExitCodeAsync(check, ct).ConfigureAwait(false) == 0;
    }

    private static async Task<bool> RouteReadyAsync(CancellationToken ct)
    {
        const string check = "test -s /run/vowin-voice-route.pid && read pid expected_start </run/vowin-voice-route.pid && " +
                             "test \"$(cut -d ' ' -f 22 /proc/$pid/stat 2>/dev/null)\" = \"$expected_start\" && " +
                             "test \"$(tr '\\000' '\\n' </proc/$pid/cmdline 2>/dev/null | sed -n '1p')\" = /tmp/vowin-call/mavo-pcm-bridge.armv7 && " +
                             "grep -q 'VoLTE route session active on hw:0,4' /run/vowin-voice-route.log && " +
                             "test \"$(cat /sys/class/android_usb/f_audio/audio_enable)\" = 1 && " +
                             "grep -q '^state: RUNNING' /proc/asound/card0/pcm4p/sub0/status && " +
                             "grep -q '^state: RUNNING' /proc/asound/card0/pcm4c/sub0/status";
        return await AdbShellExitCodeAsync(check, ct).ConfigureAwait(false) == 0;
    }

    private static async Task PushScriptAsync(string name, string contents, CancellationToken ct)
    {
        var localPath = Path.Combine(Path.GetTempPath(), $"vowin-{Guid.NewGuid():N}-{name}");
        try
        {
            await File.WriteAllTextAsync(localPath, contents.Replace("\r\n", "\n"), new System.Text.UTF8Encoding(false), ct).ConfigureAwait(false);
            await RunAdbAsync(["push", localPath, $"{RemoteDirectory}/{name}"], TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(localPath); } catch { }
        }
    }

    private static async Task<bool> VerifyAsync(string path, RuntimeFile expected, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != expected.Size) return false;
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        return hash.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> AdbShellAsync(string command, TimeSpan timeout, CancellationToken ct) =>
        (await RunAdbAsync(["shell", command], timeout, ct).ConfigureAwait(false)).Output;

    private static async Task<string> TryAdbShellAsync(string command, CancellationToken ct)
    {
        try { return await AdbShellAsync(command, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
        catch { return string.Empty; }
    }

    private static async Task<int> AdbShellExitCodeAsync(string command, CancellationToken ct)
    {
        try { return (await RunAdbAsync(["shell", command], TimeSpan.FromSeconds(10), ct, false).ConfigureAwait(false)).ExitCode; }
        catch { return -1; }
    }

    private static async Task<ProcessResult> RunAdbAsync(
        IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct, bool throwOnFailure = true)
    {
        var adb = FindAdb();
        var startInfo = new ProcessStartInfo(adb)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start adb.exe.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch
        {
            try { process.Kill(true); } catch { }
            throw;
        }
        var output = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
        if (throwOnFailure && process.ExitCode != 0)
            throw new InvalidOperationException($"adb {string.Join(' ', arguments.Take(2))} failed ({process.ExitCode}): {output.Trim()}");
        return new ProcessResult(process.ExitCode, output);
    }

    private static string FindAdb()
    {
        var candidates = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), "adb.exe")));
        candidates.AddRange(
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe"),
            @"D:\adb\adb.exe"
        ]);
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("adb.exe was not found. Install Android platform-tools or place adb.exe in PATH.");
    }

    private const string CalibrationScript = """
        #!/bin/sh
        set -e
        if ! grep -q 'ACDB -> Sent VocProc Cal!' /run/vowin-alsaucm.log 2>/dev/null; then
          rm -f /run/vowin-alsaucm.log /run/alsaucm_test
          nohup /usr/bin/alsaucm_test </dev/null >>/run/vowin-alsaucm.log 2>&1 &
          pid=$!
          n=0
          while [ $n -lt 50 ] && [ ! -p /run/alsaucm_test ]; do kill -0 $pid; sleep 0.1; n=$((n+1)); done
          [ -p /run/alsaucm_test ]
          printf 'open snd_soc_msm_9x07_Tomtom_I2S\n' > /run/alsaucm_test
          printf 'set _verb VoLTE\n' > /run/alsaucm_test
          printf 'set _enadev Auxpcm Rx\n' > /run/alsaucm_test
          printf 'set _enadev Auxpcm Tx\n' > /run/alsaucm_test
          n=0
          while [ $n -lt 100 ]; do grep -q 'ACDB -> Sent VocProc Cal!' /run/vowin-alsaucm.log && exit 0; sleep 0.1; n=$((n+1)); done
          exit 1
        fi
        """;

    private const string StartRouteScript = """
        #!/bin/sh
        set -e
        if [ -s /run/vowin-voice-route.pid ]; then
          read oldpid oldstart </run/vowin-voice-route.pid || true
          if [ "$(cut -d ' ' -f 22 /proc/$oldpid/stat 2>/dev/null)" = "$oldstart" ] &&
             [ "$(tr '\000' '\n' </proc/$oldpid/cmdline 2>/dev/null | sed -n '1p')" = /tmp/vowin-call/mavo-pcm-bridge.armv7 ] &&
             grep -q 'VoLTE route session active on hw:0,4' /run/vowin-voice-route.log 2>/dev/null &&
             [ "$(cat /sys/class/android_usb/f_audio/audio_enable 2>/dev/null)" = 1 ]; then exit 0; fi
        fi
        rm -f /run/vowin-voice-route.pid /run/vowin-voice-route.log
        nohup /tmp/vowin-call/mavo-pcm-bridge.armv7 --voice-route-session --verbose </dev/null >>/run/vowin-voice-route.log 2>&1 &
        pid=$!
        starttime=$(cut -d ' ' -f 22 /proc/$pid/stat)
        printf '%s %s\n' "$pid" "$starttime" >/run/vowin-voice-route.pid
        """;

    private const string StopRouteScript = """
        #!/bin/sh
        if [ -s /run/vowin-voice-route.pid ]; then
          read pid expected_start </run/vowin-voice-route.pid
          current_start=$(cut -d ' ' -f 22 /proc/$pid/stat 2>/dev/null)
          argv0=$(tr '\000' '\n' </proc/$pid/cmdline 2>/dev/null | sed -n '1p')
          if [ "$current_start" = "$expected_start" ] && [ "$argv0" = /tmp/vowin-call/mavo-pcm-bridge.armv7 ]; then
            kill -TERM "$pid" 2>/dev/null || true
            n=0
            while kill -0 "$pid" 2>/dev/null && [ $n -lt 50 ]; do sleep 0.1; n=$((n+1)); done
            if kill -0 "$pid" 2>/dev/null; then exit 75; fi
          fi
        fi
        rm -f /run/vowin-voice-route.pid
        echo 0 >/sys/class/android_usb/f_audio/audio_enable 2>/dev/null || true
        if [ -p /run/voc_svr ]; then printf 'T\nT\nB\n' >/run/voc_svr; fi
        """;

    private sealed record RuntimeFile(string Name, long Size, string Sha256);
    private sealed record ProcessResult(int ExitCode, string Output);
}
