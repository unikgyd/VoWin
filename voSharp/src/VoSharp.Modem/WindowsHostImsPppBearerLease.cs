using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace VoSharp.Modem;

public sealed record WindowsHostImsPppBearerInfo(
    int InterfaceIndex,
    IPAddress LocalAddress,
    IReadOnlyList<IPAddress> PcscfAddresses)
{
    public ulong? InterfaceLuid { get; init; }
}

/// <summary>
/// Opt-in owner for the guarded Windows IMS PPP handoff process. The process
/// owns modem mode restoration, a temporary RAS call and P-CSCF host routes;
/// this class holds its stdin pipe and sends STOP on disposal. Callers must
/// release the AT COM handle before StartAsync and reopen it after Ready.
/// A returned READY is not SIP registration or carrier IPsec validation.
/// </summary>
public sealed class WindowsHostImsPppBearerLease : IAsyncDisposable
{
    private const string ReadyPrefix = "VOWIN_IMS_BEARER_READY ";
    private const string MediaRoutePrefix = "VOWIN_IMS_MEDIA_ROUTE_ADDED ";
    private const string MediaRouteRemovedPrefix = "VOWIN_IMS_MEDIA_ROUTE_REMOVED ";
    private const string MediaRouteRejected = "VOWIN_IMS_MEDIA_ROUTE_REJECTED";
    private const string BearerLost = "VOWIN_IMS_BEARER_LOST";
    private const string LeaseRenewed = "VOWIN_IMS_LEASE_RENEWED";
    private const string DefaultReisolatedPrefix = "VOWIN_IMS_DEFAULT_ROUTE_REISOLATED count=";
    private readonly Process _process;
    private readonly TaskCompletionSource<WindowsHostImsPppBearerInfo> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _lost =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<string> _recent = new();
    private readonly object _logGate = new();
    private readonly object _routeAckGate = new();
    private readonly SemaphoreSlim _routeGate = new(1, 1);
    private readonly HashSet<IPAddress> _mediaRoutes = [];
    private readonly Func<WindowsHostImsPppBearerInfo, IPAddress, bool>? _testRouteVerifier;
    private readonly TimeSpan _mediaRouteAcknowledgementTimeout;
    // true = exact route ACK; false = explicit no-mutation rejection.
    private TaskCompletionSource<bool>? _pendingRouteAck;
    private TaskCompletionSource<bool>? _pendingLeaseAck;
    private IPAddress? _pendingRoute;
    private bool _pendingRouteRemoval;
    private readonly Task _stdout;
    private readonly Task _stderr;
    private readonly Task<int> _completion;
    private int _stopping;
    private int _unconfirmedRouteAbort;
    private readonly TaskCompletionSource _backgroundCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Desktop exit delegates restoration to the independent owner; explicit line stops still await it.</summary>
    public void AllowBackgroundCleanupOnApplicationExit() => _backgroundCleanup.TrySetResult();
    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatTask;
    private int _renewalCount;
    private long _lastRenewedUtcTicks;
    private int _mediaRouteCount;
    private int _defaultRouteRepairCount;

    private WindowsHostImsPppBearerLease(Process process,
        Func<WindowsHostImsPppBearerInfo, IPAddress, bool>? testRouteVerifier = null,
        TimeSpan? mediaRouteAcknowledgementTimeout = null)
    {
        _process = process;
        _testRouteVerifier = testRouteVerifier;
        _mediaRouteAcknowledgementTimeout = mediaRouteAcknowledgementTimeout ?? TimeSpan.FromSeconds(20);
        _stdout = PumpAsync(process.StandardOutput, stdout: true);
        _stderr = PumpAsync(process.StandardError, stdout: false);
        _completion = MonitorAsync();
    }

    public WindowsHostImsPppBearerInfo? Bearer { get; private set; }
    public Task<int> Completion => _completion;
    public Task Lost => _lost.Task;
    public bool IsLost => _lost.Task.IsCompleted || Completion.IsCompleted;
    public int MediaRouteCount => Volatile.Read(ref _mediaRouteCount);
    public int RenewalCount => Volatile.Read(ref _renewalCount);
    public int DefaultRouteRepairCount => Volatile.Read(ref _defaultRouteRepairCount);
    public DateTime? LastRenewedAtUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastRenewedUtcTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    // A fake owner exercises READY/ROUTE ADD/STOP framing without touching a
    // modem or route table. Production StartAsync never supplies a verifier.
    internal static async Task<WindowsHostImsPppBearerLease> StartProtocolTestOwnerAsync(
        ProcessStartInfo start,
        Func<WindowsHostImsPppBearerInfo, IPAddress, bool> routeVerifier,
        int? heartbeatLeaseSeconds = null,
        CancellationToken ct = default,
        TimeSpan? mediaRouteAcknowledgementTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(routeVerifier);
        if (mediaRouteAcknowledgementTimeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(mediaRouteAcknowledgementTimeout));
        if (!start.RedirectStandardInput || !start.RedirectStandardOutput ||
            !start.RedirectStandardError || start.UseShellExecute)
            throw new ArgumentException("The test owner requires private redirected pipes.", nameof(start));
        var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Could not launch the fake IMS PPP owner.");
            var lease = new WindowsHostImsPppBearerLease(process, routeVerifier, mediaRouteAcknowledgementTimeout);
            try
            {
                lease.Bearer = await lease._ready.Task.WaitAsync(TimeSpan.FromSeconds(10), ct)
                    .ConfigureAwait(false);
                if (heartbeatLeaseSeconds is { } seconds) lease.StartHeartbeat(seconds);
                return lease;
            }
            catch
            {
                await lease.AbortOwnerAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    public static async Task<WindowsHostImsPppBearerLease> StartAsync(
        string handoffScript, string rasProbeExecutable, string atPort,
        int maxLeaseSeconds = 3600,
        CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Host IMS PPP requires Windows RAS.");
        if (!File.Exists(handoffScript))
            throw new FileNotFoundException("The managed IMS owner must exist.");
        if (!Path.GetFileName(handoffScript).Equals("VoSharp.HostIms.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use VoSharp.HostIms.exe; PowerShell handoff scripts are no longer supported.", nameof(handoffScript));
        if (!System.Text.RegularExpressions.Regex.IsMatch(atPort, @"^COM[1-9][0-9]*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new ArgumentException("An explicit Windows COM port is required.", nameof(atPort));
        if (maxLeaseSeconds is < 15 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(maxLeaseSeconds));
        ct.ThrowIfCancellationRequested();

        await HostImsOwnerCompatibility.VerifyAsync(handoffScript, ct).ConfigureAwait(false);
        var start = new ProcessStartInfo(Path.GetFullPath(handoffScript))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "--own", atPort,
            maxLeaseSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }) start.ArgumentList.Add(argument);

        var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Could not launch the IMS PPP handoff process.");
            var lease = new WindowsHostImsPppBearerLease(process);
            try
            {
                var ready = await lease._ready.Task.WaitAsync(TimeSpan.FromMinutes(5), ct)
                    .ConfigureAwait(false);
                lease.Bearer = ready;
                lease.StartHeartbeat(maxLeaseSeconds);
                return lease;
            }
            catch
            {
                await lease.AbortOwnerAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    internal static WindowsHostImsPppBearerInfo ParseReady(string line)
    {
        if (!line.StartsWith(ReadyPrefix, StringComparison.Ordinal))
            throw new FormatException("The IMS PPP owner did not send a READY frame.");
        using var document = JsonDocument.Parse(line[ReadyPrefix.Length..]);
        var root = document.RootElement;
        var index = root.GetProperty("InterfaceIndex").GetInt32();
        var local = IPAddress.Parse(root.GetProperty("LocalAddress").GetString()!);
        var pcscf = root.GetProperty("PcscfAddresses").EnumerateArray()
            .Select(item => IPAddress.Parse(item.GetString()!)).Distinct().ToArray();
        if (index <= 0 || !IsUsableIpv6(local) || pcscf.Length is < 1 or > 4 ||
            pcscf.Any(address => !IsUsableIpv6(address) || address.Equals(local)))
            throw new FormatException("The IMS PPP owner reported an invalid bearer endpoint.");
        ulong? luid = root.TryGetProperty("InterfaceLuid", out var ownerLuid) ? ownerLuid.GetUInt64() : null;
        if (luid is 0) throw new FormatException("The IMS PPP owner reported an invalid interface LUID.");
        return new WindowsHostImsPppBearerInfo(index, local, pcscf) { InterfaceLuid = luid };
    }

    private static bool IsUsableIpv6(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 &&
        address.ScopeId == 0 &&
        !address.IsIPv4MappedToIPv6 && !address.IsIPv6LinkLocal &&
        !address.IsIPv6Multicast && !IPAddress.IsLoopback(address) &&
        !address.Equals(IPAddress.IPv6Any);

    /// <summary>
    /// Adds exactly one SDP-advertised media destination to the isolated PPP.
    /// The handoff owner verifies the route and removes it with the bearer.
    /// No IPv6 default route is installed.
    /// </summary>
    public async Task EnsureMediaRouteAsync(IPAddress destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var bearer = Bearer ?? throw new InvalidOperationException("IMS PPP is not ready.");
        if (!IsUsableIpv6(destination) || destination.Equals(bearer.LocalAddress))
            throw new ArgumentException("IMS media requires a usable remote IPv6 address.", nameof(destination));
        await _routeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _stopping) != 0 || IsLost)
                throw new InvalidOperationException("IMS PPP owner is stopping; no media route can be added.");
            if (_mediaRoutes.Contains(destination))
            {
                VerifyMediaRoute(bearer, destination);
                return;
            }
            if (_mediaRoutes.Count >= 8 && !bearer.PcscfAddresses.Contains(destination))
                throw new InvalidOperationException("IMS PPP media route limit of eight destinations was reached.");

            var acknowledgement = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_routeAckGate)
            {
                _pendingRoute = destination;
                _pendingRouteAck = acknowledgement;
                _pendingRouteRemoval = false;
            }
            var commandStarted = false;
            var rejected = false;
            try
            {
                ct.ThrowIfCancellationRequested();
                // A failed/canceled write can still have delivered the command.
                // From this point, only an explicit rejection proves no route exists.
                commandStarted = true;
                await _process.StandardInput.WriteLineAsync($"ROUTE ADD {destination}".AsMemory(), ct)
                    .ConfigureAwait(false);
                rejected = !await acknowledgement.Task.WaitAsync(_mediaRouteAcknowledgementTimeout, ct)
                    .ConfigureAwait(false);
                if (rejected)
                    throw new InvalidOperationException(
                        "IMS PPP owner rejected the media route without changing bearer isolation.");
                VerifyMediaRoute(bearer, destination);
                if (!bearer.PcscfAddresses.Contains(destination))
                {
                    _mediaRoutes.Add(destination);
                    Volatile.Write(ref _mediaRouteCount, _mediaRoutes.Count);
                }
            }
            catch
            {
                // The owner may already have installed the route before its ACK.
                // Never drop that unknown state and let another call/ACK reuse it.
                if (commandStarted && !rejected && !IsLost)
                    FenceUnconfirmedMediaRoute();
                throw;
            }
            finally
            {
                lock (_routeAckGate)
                {
                    _pendingRoute = null;
                    _pendingRouteAck = null;
                    _pendingRouteRemoval = false;
                }
            }
        }
        finally { _routeGate.Release(); }
    }

    /// <summary>Remove only media routes created by this owner after a call releases its RTP session.</summary>
    public async Task ClearMediaRoutesAsync(CancellationToken ct = default)
    {
        try
        {
            await _routeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _stopping) != 0 || IsLost)
                    throw new InvalidOperationException("IMS PPP owner is stopping; media routes cannot be cleared.");
                foreach (var destination in _mediaRoutes.ToArray())
                {
                    var acknowledgement = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_routeAckGate)
                    {
                        _pendingRoute = destination;
                        _pendingRouteAck = acknowledgement;
                        _pendingRouteRemoval = true;
                    }
                    try
                    {
                        await _process.StandardInput.WriteLineAsync($"ROUTE REMOVE {destination}")
                            .ConfigureAwait(false);
                        if (!await acknowledgement.Task.WaitAsync(_mediaRouteAcknowledgementTimeout, ct)
                                .ConfigureAwait(false))
                            throw new InvalidOperationException("IMS PPP owner rejected media route removal.");
                        _mediaRoutes.Remove(destination);
                        Volatile.Write(ref _mediaRouteCount, _mediaRoutes.Count);
                    }
                    finally
                    {
                        lock (_routeAckGate)
                        {
                            _pendingRoute = null;
                            _pendingRouteAck = null;
                            _pendingRouteRemoval = false;
                        }
                    }
                }
            }
            finally { _routeGate.Release(); }
        }
        catch
        {
            // An unconfirmed route deletion leaves the PPP isolation state
            // ambiguous. Wake the independent owner-exit recovery watchdog.
            if (Volatile.Read(ref _stopping) == 0 && !IsLost)
                await AbortOwnerAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void VerifyMediaRoute(WindowsHostImsPppBearerInfo bearer, IPAddress destination)
    {
        if (_testRouteVerifier is { } testVerifier)
        {
            if (!testVerifier(bearer, destination))
                throw new InvalidOperationException("The fake IMS PPP route verifier rejected the media destination.");
            return;
        }
        var route = HostImsRouteVerifier.Check(
            [new HostImsEndpointCandidate(5, bearer.LocalAddress, destination)],
            bearer.InterfaceIndex, bearer.InterfaceLuid);
        if (route.Count != 1 || !route[0].IsVerified ||
            route[0].InterfaceIndex != bearer.InterfaceIndex)
            throw new InvalidOperationException(
                "Windows did not select the exact IMS PPP interface and source for the media route.");
    }

    private void FenceUnconfirmedMediaRoute()
    {
        if (Interlocked.Exchange(ref _unconfirmedRouteAbort, 1) != 0) return;
        _lost.TrySetResult(true); // Fence synchronously before releasing the command gate.
        _ = Task.Run(async () =>
        {
            try { await AbortOwnerAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                // Cancellation must not wait for native process exit/restoration.
                // If abort itself fails, keep Lost set and let explicit disposal retry.
                lock (_logGate)
                {
                    if (_recent.Count == 24) _recent.Dequeue();
                    _recent.Enqueue($"IMS PPP unconfirmed media route abort failed: {ex.GetType().Name}.");
                }
            }
        });
    }

    private void StartHeartbeat(int maxLeaseSeconds)
    {
        var heartbeatSeconds = Math.Clamp(maxLeaseSeconds / 3, 5, 300);
        _heartbeatCts = new CancellationTokenSource();
        var token = _heartbeatCts.Token;
        _heartbeatTask = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(heartbeatSeconds), token).ConfigureAwait(false);
                    await RenewLeaseAsync(token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                lock (_logGate)
                {
                    if (_recent.Count == 24) _recent.Dequeue();
                    _recent.Enqueue($"IMS PPP heartbeat failed: {ex.GetType().Name}: {ex.Message}");
                }
                // A failed heartbeat must not leave modem-internal IMS disabled.
                // Killing only this owned process wakes its independent recovery watchdog.
                await AbortOwnerAsync().ConfigureAwait(false);
            }
        });
    }

    internal async Task RenewLeaseAsync(CancellationToken ct = default)
    {
        await _routeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _stopping) != 0 || IsLost)
                throw new InvalidOperationException("IMS PPP owner is stopping; the lease cannot be renewed.");
            var acknowledgement = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_routeAckGate) _pendingLeaseAck = acknowledgement;
            try
            {
                await _process.StandardInput.WriteLineAsync("RENEW".AsMemory(), ct)
                    .ConfigureAwait(false);
                await acknowledgement.Task.WaitAsync(TimeSpan.FromSeconds(20), ct)
                    .ConfigureAwait(false);
                Interlocked.Exchange(ref _lastRenewedUtcTicks, DateTime.UtcNow.Ticks);
                Interlocked.Increment(ref _renewalCount);
            }
            finally
            {
                lock (_routeAckGate) _pendingLeaseAck = null;
            }
        }
        finally { _routeGate.Release(); }
    }

    private async Task PumpAsync(StreamReader reader, bool stdout)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_logGate)
                {
                    if (_recent.Count == 24) _recent.Dequeue();
                    _recent.Enqueue(line);
                }
                if (line.StartsWith(ReadyPrefix, StringComparison.Ordinal))
                {
                    try { _ready.TrySetResult(ParseReady(line)); }
                    catch (Exception ex) { _ready.TrySetException(ex); }
                }
                if (stdout && (line.StartsWith(MediaRoutePrefix, StringComparison.Ordinal) ||
                    line.StartsWith(MediaRouteRemovedPrefix, StringComparison.Ordinal)))
                {
                    lock (_routeAckGate)
                    {
                        var removed = line.StartsWith(MediaRouteRemovedPrefix, StringComparison.Ordinal);
                        var prefixLength = removed ? MediaRouteRemovedPrefix.Length : MediaRoutePrefix.Length;
                        if (removed == _pendingRouteRemoval &&
                            IPAddress.TryParse(line[prefixLength..], out var address) &&
                            address.Equals(_pendingRoute))
                            _pendingRouteAck?.TrySetResult(true);
                        else
                            _pendingRouteAck?.TrySetException(
                                new FormatException("IMS PPP owner acknowledged the wrong media route."));
                    }
                }
                if (stdout && line.Equals(MediaRouteRejected, StringComparison.Ordinal))
                {
                    lock (_routeAckGate)
                        _pendingRouteAck?.TrySetResult(false);
                }
                if (stdout && line.Equals(BearerLost, StringComparison.Ordinal))
                {
                    _lost.TrySetResult(true);
                    lock (_routeAckGate)
                    {
                        _pendingRouteAck?.TrySetException(new InvalidOperationException(
                            "IMS PPP bearer was lost before media route confirmation."));
                        _pendingLeaseAck?.TrySetException(new InvalidOperationException(
                            "IMS PPP bearer was lost before lease renewal."));
                    }
                }
                if (stdout && line.Equals(LeaseRenewed, StringComparison.Ordinal))
                {
                    lock (_routeAckGate) _pendingLeaseAck?.TrySetResult(true);
                }
                if (stdout && line.StartsWith(DefaultReisolatedPrefix, StringComparison.Ordinal) &&
                    int.TryParse(line.AsSpan(DefaultReisolatedPrefix.Length),
                        System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var repairs) &&
                    repairs > DefaultRouteRepairCount)
                    Volatile.Write(ref _defaultRouteRepairCount, repairs);
            }
        }
        catch (IOException ex) { _ready.TrySetException(ex); }
    }

    private async Task<int> MonitorAsync()
    {
        await _process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(_stdout, _stderr).ConfigureAwait(false);
        _lost.TrySetResult(true);
        lock (_routeAckGate)
        {
            _pendingRouteAck?.TrySetException(new InvalidOperationException(
                $"IMS PPP owner exited before media route confirmation: {RecentLog()}"));
            _pendingLeaseAck?.TrySetException(new InvalidOperationException(
                $"IMS PPP owner exited before lease renewal: {RecentLog()}"));
        }
        if (!_ready.Task.IsCompleted)
            _ready.TrySetException(new InvalidOperationException(
                $"IMS PPP handoff exited before READY (code {_process.ExitCode}): {RecentLog()}"));
        return _process.ExitCode;
    }

    private string RecentLog()
    {
        lock (_logGate) return string.Join(" | ", _recent);
    }

    private async Task AbortOwnerAsync()
    {
        if (!_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: false); }
            catch (InvalidOperationException) { }
        }
        try { await _completion.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
        try
        {
            if (_heartbeatCts is { } heartbeatCts)
            {
                heartbeatCts.Cancel();
                if (_heartbeatTask is { } heartbeatTask)
                    await heartbeatTask.ConfigureAwait(false);
                heartbeatCts.Dispose();
            }
            await _routeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_process.HasExited)
                {
                    try
                    {
                        await _process.StandardInput.WriteLineAsync("STOP").ConfigureAwait(false);
                        _process.StandardInput.Close();
                    }
                    catch (IOException) { }
                }
            }
            finally { _routeGate.Release(); }
            try
            {
                var finished = await Task.WhenAny(_completion, _backgroundCleanup.Task)
                    .WaitAsync(TimeSpan.FromMinutes(5)).ConfigureAwait(false);
                if (finished == _backgroundCleanup.Task && !_completion.IsCompleted) return;
                var code = await _completion.ConfigureAwait(false);
                if (code != 0)
                    throw new InvalidOperationException($"IMS PPP handoff exited with code {code}: {RecentLog()}");
            }
            catch (TimeoutException)
            {
                await AbortOwnerAsync().ConfigureAwait(false);
                throw new TimeoutException("IMS PPP handoff did not restore the modem within five minutes; its independent watchdog will attempt recovery.");
            }
        }
        finally
        {
            if (_backgroundCleanup.Task.IsCompleted && !_completion.IsCompleted)
                _ = _completion.ContinueWith(completed =>
                {
                    _ = completed.Exception;
                    _process.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            else _process.Dispose();
        }
    }
}
