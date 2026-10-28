using System.Collections.Concurrent;
using System.IO.Ports;
using System.Security.Cryptography;
using System.Text;
using VoSharp.Common.Events;
using VoSharp.Kernel.Events;
using VoSharp.Sim.Pcsc;
using VoSharp.Modem;
using qmiSharp.Transport;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Kernel.Pool;

/// <summary>
/// Manages multi-modem hardware slots, automated serial port discovery,
/// per-SIM dedicated SOCKS5 proxy routing, and least-cost destination routing.
/// </summary>
public class ModemPool : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ModemSlot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly AsyncEventBus _eventBus;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    // Keep gates for the pool lifetime. Replacing a gate after removal would let
    // old waiters and a newly added slot operate on the same device concurrently.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _slotLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _portLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<ModemSlot, CancellationToken, Task<bool>> _attachSlot;
    private readonly IkeRetryGate _ikeRetryGate;
    private readonly TaskCompletionSource _operationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _operationCount;
    private bool _stopping;
    private bool _applicationExitRequested;
    private Task? _disposeTask;

    private string? _activeSlotId;

    public event EventHandler<ModemSlot>? SlotAdded;
    public event EventHandler<string>? SlotRemoved;
    public event EventHandler<ActiveSlotChangedEventArgs>? ActiveSlotChanged;
    public event EventHandler<SlotStateChangedEventArgs>? SlotStateChanged;
    public event EventHandler<SlotListChangedEventArgs>? SlotListChanged;

    public IReadOnlyDictionary<string, ModemSlot> Slots => _slots;
    private volatile ModemControlPreference _controlPreference;
    public ModemControlPreference ControlPreference
    {
        get => _controlPreference;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _controlPreference = value;
            foreach (var slot in _slots.Values) slot.ControlPreference = value;
        }
    }
    public string? ActiveSlotId => _activeSlotId;

    public ModemSlot? ActiveSlot
    {
        get
        {
            if (_activeSlotId != null && _slots.TryGetValue(_activeSlotId, out var slot))
                return slot;
            return _slots.Values.FirstOrDefault(s => s.State == SlotState.Online) ?? _slots.Values.FirstOrDefault();
        }
    }

    public ModemPool(AsyncEventBus? eventBus = null, IkeRetryGate? ikeRetry = null)
        : this(eventBus, (slot, ct) => slot.AttachAsync(ct), ikeRetry)
    {
    }

    internal ModemPool(AsyncEventBus? eventBus, Func<ModemSlot, CancellationToken, Task<bool>> attachSlot, IkeRetryGate? ikeRetry = null)
    {
        _eventBus = eventBus ?? new AsyncEventBus();
        _attachSlot = attachSlot;
        _ikeRetryGate = ikeRetry ?? new(new MemoryIkeRetryJournalStore(), new WindowsIkeRetryClock());
    }

    internal ModemSlot CreateSlot(string id, string port, int baud, string? name = null, string? proxy = null)
        => new(id, port, baud, name, proxy, _eventBus, ikeRetry: _ikeRetryGate);

    /// <summary>
    /// Adds or updates a modem slot, attaching to the serial port.
    /// </summary>
    public Task<ModemSlot> AddOrUpdateSlotAsync(
        string portName,
        int baudRate = 115200,
        string? name = null,
        string? slotId = null,
        string? proxyUrl = null,
        CancellationToken ct = default) =>
        AddOrUpdateSerialSlotAsync(portName, baudRate, name, slotId, proxyUrl, refreshExisting: false, ct);

    private async Task<ModemSlot> AddOrUpdateSerialSlotAsync(
        string portName, int baudRate, string? name, string? slotId, string? proxyUrl,
        bool refreshExisting, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        if (slotId is not null) ArgumentException.ThrowIfNullOrWhiteSpace(slotId);
        portName = portName.Trim();
        using var operation = BeginOperation();

        // A manually named slot and discovery of its COM port must converge on
        // the same instance. Resolve again after waiting if another add won.
        while (true)
        {
            var id = slotId ?? FindPortOwner(portName)?.Id ?? portName.ToLowerInvariant();
            using var mutation = await AcquireMutationAsync(id, portName, ct).ConfigureAwait(false);
            if (FindPortOwner(portName) is { } owner && !owner.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                if (slotId is null) continue;
                throw new InvalidOperationException($"Port {portName} is already managed by slot {owner.Id}.");
            }

            if (_slots.TryGetValue(id, out var existing))
            {
                if (existing.IsHostImsPppTransitionOrActive)
                    return existing;
                if (existing.State == SlotState.Busy)
                {
                    if (!existing.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase) || existing.BaudRate != baudRate)
                        throw new InvalidOperationException("Cannot change the serial connection of a busy modem.");
                    // A scan or duplicate add must not tear down an ongoing call.
                    if (!string.IsNullOrWhiteSpace(name)) existing.Name = name;
                    return existing;
                }
                if (refreshExisting && existing.State == SlotState.Online)
                {
                    var responsive = false;
                    try { responsive = existing.Modem?.IsOpen == true && await existing.Modem.PingAsync(ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch { }
                    if (responsive)
                    {
                        await existing.RefreshMetricsAsync(ct).ConfigureAwait(false);
                        return existing;
                    }
                }
                if (existing.Modem != null)
                    await existing.DetachHardwareAsync(ct).ConfigureAwait(false);
                existing.PortName = portName;
                existing.BaudRate = baudRate;
                if (!string.IsNullOrWhiteSpace(name)) existing.Name = name;
                if (proxyUrl != null) existing.ProxyUrl = proxyUrl;
                await _attachSlot(existing, ct).ConfigureAwait(false);
                try { SlotListChanged?.Invoke(this, new SlotListChangedEventArgs(_slots.Values.ToList())); } catch { }
                _eventBus.Publish("pool.slot.reattached", "ModemPool", existing.GetDiagnosticInfo());
                return existing;
            }

            var slot = CreateSlot(id, portName, baudRate, name, proxyUrl);
            slot.ControlPreference = ControlPreference;
            slot.StateChanged += (s, e) =>
            {
                try { SlotStateChanged?.Invoke(this, e); } catch { }
            };

            try
            {
                await _attachSlot(slot, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
            catch
            {
                await slot.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            lock (_lock)
            {
                _slots[id] = slot;
                slot.ControlPreference = ControlPreference;
                if (_activeSlotId == null || !_slots.ContainsKey(_activeSlotId))
                {
                    _activeSlotId = id;
                    slot.IsActive = true;
                    try { ActiveSlotChanged?.Invoke(this, new ActiveSlotChangedEventArgs(null, id, slot)); } catch { }
                }
            }

            try { SlotAdded?.Invoke(this, slot); } catch { }
            try { SlotListChanged?.Invoke(this, new SlotListChangedEventArgs(_slots.Values.ToList())); } catch { }
            _eventBus.Publish("pool.slot.added", "ModemPool", slot.GetDiagnosticInfo());
            return slot;
        }
    }

    /// <summary>
    /// Attaches an already constructed ModemSlot directly (useful for testing and mock topologies).
    /// </summary>
    public bool TryAddSlot(ModemSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        using var operation = BeginOperation();
        var idGate = _slotLocks.GetOrAdd(slot.Id, _ => new SemaphoreSlim(1, 1));
        if (!idGate.Wait(0)) return false;
        using var idLease = new GateLease([idGate]);
        var portGate = _portLocks.GetOrAdd(slot.PortName, _ => new SemaphoreSlim(1, 1));
        if (!portGate.Wait(0)) return false;
        using var portLease = new GateLease([portGate]);
        if (FindPortOwner(slot.PortName) is not null) return false;
        if (_slots.TryAdd(slot.Id, slot))
        {
            slot.ControlPreference = ControlPreference;
            lock (_lock)
            {
                if (_activeSlotId == null)
                {
                    _activeSlotId = slot.Id;
                    slot.IsActive = true;
                }
            }
            try { SlotAdded?.Invoke(this, slot); } catch { }
            try { SlotListChanged?.Invoke(this, new SlotListChangedEventArgs(_slots.Values.ToList())); } catch { }
            return true;
        }
        return false;
    }

    /// <summary>
    /// Acquires an exclusive async lease on a slot for safe multi-operation execution.
    /// </summary>
    public async Task<IAsyncDisposable?> AcquireSlotLeaseAsync(string slotId, int timeoutMs = 5000, CancellationToken ct = default)
    {
        var operation = BeginOperation();
        try
        {
            if (!_slots.TryGetValue(slotId, out var slot)) return null;
            var sem = _slotLocks.GetOrAdd(slotId, _ => new SemaphoreSlim(1, 1));
            if (!await sem.WaitAsync(timeoutMs, ct).ConfigureAwait(false)) return null;
            if (!_slots.TryGetValue(slotId, out var current) || !ReferenceEquals(current, slot))
            {
                sem.Release();
                return null;
            }
            var lease = new SlotLease(sem, operation);
            operation = null!; // The returned lease owns the lifetime reservation.
            return lease;
        }
        finally { operation?.Dispose(); }
    }

    /// <summary>
    /// Removes and disposes a slot by ID with graceful draining of active leases.
    /// </summary>
    public Task<bool> RemoveSlotAsync(string slotId, CancellationToken ct = default) =>
        RemoveSlotCoreAsync(slotId, ct);

    private async Task<bool> RemoveSlotCoreAsync(string slotId, CancellationToken ct, ModemSlot? failedProbe = null)
    {
        using var operation = BeginOperation();
        using var mutation = await AcquireMutationAsync(slotId, null, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (_slots.TryGetValue(slotId, out var slot))
        {
            if (failedProbe is not null && (!ReferenceEquals(slot, failedProbe) ||
                slot.State is SlotState.Online or SlotState.Busy || slot.IsHostImsPppTransitionOrActive)) return false;
            // Stop new routing immediately after leases drain. The mutation
            // still holds both ID and port gates until hardware is fully closed.
            // Cancellation cannot leave a half-disposed slot once this starts.
            lock (_lock)
            {
                _slots.TryRemove(slotId, out _);
                slot.IsActive = false;
                if (_activeSlotId != null && _activeSlotId.Equals(slotId, StringComparison.OrdinalIgnoreCase))
                {
                    var prevId = _activeSlotId;
                    _activeSlotId = _slots.Keys.FirstOrDefault();
                    ModemSlot? nextActive = null;
                    if (_activeSlotId != null && _slots.TryGetValue(_activeSlotId, out nextActive))
                    {
                        nextActive.IsActive = true;
                    }
                    try { ActiveSlotChanged?.Invoke(this, new ActiveSlotChangedEventArgs(prevId, _activeSlotId, nextActive)); } catch { }
                }
            }

            await slot.DisposeAsync().ConfigureAwait(false);
            try { SlotRemoved?.Invoke(this, slotId); } catch { }
            try { SlotListChanged?.Invoke(this, new SlotListChangedEventArgs(_slots.Values.ToList())); } catch { }
            _eventBus.Publish("pool.slot.removed", "ModemPool", slotId);
            return true;
        }

        return false;
    }

    private sealed class SlotLease(SemaphoreSlim semaphore, PoolOperation operation) : IAsyncDisposable, IDisposable
    {
        private int _disposed;
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                semaphore.Release();
                operation.Dispose();
            }
        }
    }

    private ModemSlot? FindPortOwner(string portName) =>
        _slots.Values.FirstOrDefault(slot => slot.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase));

    private async Task<GateLease> AcquireMutationAsync(string slotId, string? requestedPort, CancellationToken ct)
    {
        var held = new List<SemaphoreSlim>();
        try
        {
            var idGate = _slotLocks.GetOrAdd(slotId, _ => new SemaphoreSlim(1, 1));
            await idGate.WaitAsync(ct).ConfigureAwait(false);
            held.Add(idGate);
            var currentPort = _slots.TryGetValue(slotId, out var slot) ? slot.PortName : null;
            // A move reserves both ports. Sorted acquisition avoids opposite
            // port moves deadlocking; independent modems still run in parallel.
            foreach (var port in new[] { currentPort, requestedPort }.OfType<string>()
                         .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            {
                var gate = _portLocks.GetOrAdd(port, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(ct).ConfigureAwait(false);
                held.Add(gate);
            }
            return new GateLease(held);
        }
        catch
        {
            new GateLease(held).Dispose();
            throw;
        }
    }

    private sealed class GateLease(IReadOnlyList<SemaphoreSlim> held) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            for (var i = held.Count - 1; i >= 0; i--) held[i].Release();
        }
    }

    private PoolOperation BeginOperation()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_stopping || _applicationExitRequested, this);
            _operationCount++;
            return new PoolOperation(this);
        }
    }

    private sealed class PoolOperation(ModemPool pool) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (pool._lock)
            {
                if (--pool._operationCount == 0 && pool._stopping) pool._operationsDrained.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Selects the primary active slot.
    /// </summary>
    public bool SelectSlot(string slotId)
    {
        ModemSlot? slot;
        string? prevId;
        lock (_lock)
        {
            if (_stopping || !_slots.TryGetValue(slotId, out slot)) return false;
            prevId = _activeSlotId;
            foreach (var s in _slots.Values) s.IsActive = false;
            _activeSlotId = slot.Id;
            slot.IsActive = true;
        }
        try { ActiveSlotChanged?.Invoke(this, new ActiveSlotChangedEventArgs(prevId, slot.Id, slot)); } catch { }
        _eventBus.Publish("pool.slot.selected", "ModemPool", slotId);
        return true;
    }

    /// <summary>
    /// Configures a dedicated SOCKS5 proxy URL for a specific slot.
    /// </summary>
    public bool SetSlotProxy(string slotId, string? proxyUrl)
    {
        if (_slots.TryGetValue(slotId, out var slot))
        {
            var normalized = string.IsNullOrWhiteSpace(proxyUrl) ||
                             proxyUrl.Equals("direct", StringComparison.OrdinalIgnoreCase)
                ? null
                : proxyUrl.Trim();
            if (normalized is not null && !IsValidSocks5Proxy(normalized))
            {
                _eventBus.Publish(EventTopics.SystemError, "ModemPool",
                    $"Rejected non-SOCKS proxy for slot {slotId}. VoWiFi requires socks://, socks5://, or socks5h:// because IKEv2 uses UDP ASSOCIATE.");
                return false;
            }

            slot.ProxyUrl = normalized;
            slot.VoWifi.ProxyUrl = normalized;
            _eventBus.Publish("pool.slot.proxy_updated", "ModemPool", new { SlotId = slotId, ProxyUrl = normalized ?? "direct" });
            return true;
        }

        return false;
    }

    private static bool IsValidSocks5Proxy(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme.Equals("socks", StringComparison.OrdinalIgnoreCase) ||
         uri.Scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase) ||
         uri.Scheme.Equals("socks5h", StringComparison.OrdinalIgnoreCase)) &&
        !string.IsNullOrWhiteSpace(uri.Host) && uri.Port is > 0 and <= 65535;

    public async Task<IReadOnlyList<ModemSlot>> DiscoverAndEnrichAsync(CancellationToken ct = default)
    {
        using var operation = BeginOperation();
        // SerialPort.Open and the first AT read can block synchronously before
        // their async wrappers yield.  Keep concurrent discovery requests from
        // fighting over ports, while each individual port probe runs on the
        // thread pool below.
        await _discoveryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
        // Unclassified sibling AT ports may belong to the same rebooting modem.
        // Do not open them or run SIM/APDU probes against an owned IMS bearer.
        var ownedSlots = _slots.Values.Where(slot => slot.IsHostImsPppTransitionOrActive).ToArray();
        IReadOnlyList<WindowsUsbFunction> inventory;
        try { inventory = WindowsUsbTopology.EnumeratePresent(); }
        catch { inventory = []; }
        if (ownedSlots.Any(slot => (slot.UsbContainerId ?? ModemDiscoveryPolicy.FindContainer(inventory, slot.PortName)) is null))
            return _slots.Values.Where(slot => slot.IsHostImsPppTransitionOrActive ||
                slot.State is SlotState.Online or SlotState.Busy).ToArray();
        string[] portNames;

        try
        {
            portNames = SerialPort.GetPortNames();
        }
        catch
        {
            portNames = Array.Empty<string>();
        }

        var probePorts = ModemDiscoveryPolicy.SelectPorts(portNames, inventory,
            ownedSlots.Select(slot => (slot.PortName, slot.UsbContainerId)));
        var tasks = probePorts
            // SerialPort's Windows driver can block a worker thread for the
            // entire probe timeout. LongRunning gives each candidate port a
            // dedicated thread so one dead Bluetooth/debug port cannot starve
            // the remaining probes in the thread pool.
            .Select(port => Task.Factory.StartNew(async () =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                // A responsive modem needs additional time after the AT ping to
                // read SIM identity and arm CLIP/CNMI unsolicited notifications.
                // The old 1.5 s budget often cancelled this setup halfway through.
                cts.CancelAfter(15000);

                var slot = await AddOrUpdateSerialSlotAsync(port, 115200, null, null, null,
                    refreshExisting: true, cts.Token).ConfigureAwait(false);
                if (slot.State is SlotState.Online or SlotState.Busy || slot.IsHostImsPppTransitionOrActive)
                {
                    return slot;
                }
                else
                {
                    await RemoveSlotCoreAsync(slot.Id, ct, failedProbe: slot).ConfigureAwait(false);
                }
            }
            catch
            {
                // Port was not a responsive AT modem or was busy
            }
            return null;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap());

        var probedSlots = await Task.WhenAll(tasks).ConfigureAwait(false);
        var validSlots = probedSlots.Where(s => s != null).Concat(ownedSlots).ToList();

        // Deduplicate physical devices, never SIM identities shared across devices.
        var discovered = await DeduplicateDiscoveredSlotsAsync(validSlots.OfType<ModemSlot>(), ct).ConfigureAwait(false);

        // PC/SC readers are not serial ports, so include them in the same user
        // initiated scan. A reader becomes a normal slot only after EF.ICCID,
        // EF.IMSI and ADF.USIM have all been verified.
        foreach (var readerName in ListPcscReaders())
        {
            var pcscSlot = await AddOrUpdatePcscSlotAsync(readerName, ct).ConfigureAwait(false);
            if (pcscSlot != null)
                discovered.Add(pcscSlot);
        }

        return discovered;
        }
        finally
        {
            _discoveryGate.Release();
        }
    }

    private static IReadOnlyList<string> ListPcscReaders()
    {
        try
        {
            using var reader = new PcscReader();
            return reader.Initialize() ? reader.ListReaders() : Array.Empty<string>();
        }
        catch
        {
            // The Smart Card service can legitimately be disabled on machines
            // that only use serial modems. Do not make that break COM discovery.
            return Array.Empty<string>();
        }
    }

    internal async Task<List<ModemSlot>> DeduplicateDiscoveredSlotsAsync(IEnumerable<ModemSlot> candidates, CancellationToken ct = default)
    {
        var discovered = new List<ModemSlot>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Retained identity on an offline port must not defeat a newly verified
        // live sibling. Stable ordering otherwise preserves the existing selection.
        foreach (var slot in candidates.DistinctBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(s => s.IsHostImsPppTransitionOrActive)
                     .ThenByDescending(s => s.State is SlotState.Online or SlotState.Busy)
                     .ThenByDescending(s => s.Modem?.IsOpen == true).ThenByDescending(s => s.Sim != null))
        {
            var identity = slot.UsbContainerId is { } container ? $"usb:{container}" :
                !string.IsNullOrWhiteSpace(slot.Imei) ? $"imei:{slot.Imei}" : $"slot:{slot.Id}";
            if (seen.Add(identity)) discovered.Add(slot);
            else await RemoveSlotAsync(slot.Id, ct).ConfigureAwait(false);
        }
        return discovered;
    }

    private async Task<ModemSlot?> AddOrUpdatePcscSlotAsync(string readerName, CancellationToken ct)
    {
        using var operation = BeginOperation();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(readerName)))[..12].ToLowerInvariant();
        var id = $"pcsc-{fingerprint}";
        using var mutation = await AcquireMutationAsync(id, $"PC/SC · {readerName}", ct).ConfigureAwait(false);

        if (_slots.TryGetValue(id, out var existing))
        {
            if (!existing.IsPcscReader) return null;
            return await existing.AttachPcscAsync(readerName, ct).ConfigureAwait(false) ? existing : null;
        }

        var slot = CreateSlot(id, $"PC/SC · {readerName}", 0, "PC/SC 读卡器");
        slot.StateChanged += (_, args) =>
        {
            try { SlotStateChanged?.Invoke(this, args); } catch { }
        };

        if (!await slot.AttachPcscAsync(readerName, ct).ConfigureAwait(false))
        {
            await slot.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        if (!_slots.TryAdd(id, slot))
        {
            await slot.DisposeAsync().ConfigureAwait(false);
            return _slots.TryGetValue(id, out var concurrent) ? concurrent : null;
        }

        lock (_lock)
        {
            if (_activeSlotId == null || !_slots.ContainsKey(_activeSlotId))
            {
                _activeSlotId = id;
                slot.IsActive = true;
                try { ActiveSlotChanged?.Invoke(this, new ActiveSlotChangedEventArgs(null, id, slot)); } catch { }
            }
        }

        try { SlotAdded?.Invoke(this, slot); } catch { }
        try { SlotListChanged?.Invoke(this, new SlotListChangedEventArgs(_slots.Values.ToList())); } catch { }
        _eventBus.Publish("pool.slot.added", "ModemPool", slot.GetDiagnosticInfo());
        return slot;
    }

    /// <summary>
    /// Picks the best slot to dial or send SMS based on the target number prefix or active slot.
    /// </summary>
    public ModemSlot? FindSlotForTarget(string targetNumber)
    {
        var clean = targetNumber.Trim().TrimStart('+');

        // PH (+63 / 515)
        if (clean.StartsWith("63") || clean.Length == 10 && clean.StartsWith("9"))
        {
            var phSlot = _slots.Values.FirstOrDefault(s => s.Sim?.Mcc == "515" && s.State != SlotState.Error);
            if (phSlot != null) return phSlot;
        }

        // UK (+44 / 234, 235)
        if (clean.StartsWith("44") || clean.StartsWith("07"))
        {
            var ukSlot = _slots.Values.FirstOrDefault(s => (s.Sim?.Mcc is "234" or "235") && s.State != SlotState.Error);
            if (ukSlot != null) return ukSlot;
        }

        // CN (+86 / 460, 461)
        if (clean.StartsWith("86") || clean.Length == 11 && clean.StartsWith("1"))
        {
            var cnSlot = _slots.Values.FirstOrDefault(s => (s.Sim?.Mcc is "460" or "461") && s.State != SlotState.Error);
            if (cnSlot != null) return cnSlot;
        }

        // Fallback to active slot or first available slot
        return ActiveSlot ?? _slots.Values.FirstOrDefault(s => s.State != SlotState.Error) ?? _slots.Values.FirstOrDefault();
    }

    public IReadOnlyList<object> GetSlotsDiagnostic()
    {
        return _slots.Values.Select(s => s.GetDiagnosticInfo()).ToList();
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Task operationsDrained;
        lock (_lock)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _stopping = true;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
            operationsDrained = _operationCount == 0 ? Task.CompletedTask : _operationsDrained.Task;
        }
        _ = DisposeCoreAsync(operationsDrained, completion);
        return new ValueTask(completion.Task);
    }

    public void PrepareForApplicationExit()
    {
        lock (_lock)
        {
            _applicationExitRequested = true;
            foreach (var slot in _slots.Values) slot.PrepareForApplicationExit();
        }
    }

    private async Task DisposeCoreAsync(Task operationsDrained, TaskCompletionSource completion)
    {
        try
        {
            await operationsDrained.ConfigureAwait(false);
            // Each independent line unregisters in parallel on application exit;
            // none waits for its modem's re-enumeration or restoration.
            async Task StopSlot(ModemSlot slot)
            {
                if (_applicationExitRequested) slot.PrepareForApplicationExit();
                try { await slot.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            if (_applicationExitRequested) await Task.WhenAll(_slots.Values.Select(StopSlot)).ConfigureAwait(false);
            else foreach (var slot in _slots.Values) await StopSlot(slot).ConfigureAwait(false);
            _slots.Clear();
            _activeSlotId = null;
            foreach (var gate in _slotLocks.Values) gate.Dispose();
            foreach (var gate in _portLocks.Values) gate.Dispose();
            _discoveryGate.Dispose();
            completion.TrySetResult();
        }
        catch (Exception ex) { completion.TrySetException(ex); }
    }
}
