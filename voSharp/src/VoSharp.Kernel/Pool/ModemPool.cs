using System.Collections.Concurrent;
using System.IO.Ports;
using VoSharp.Common.Events;
using VoSharp.Kernel.Events;

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

    private string? _activeSlotId;

    public event EventHandler<ModemSlot>? SlotAdded;
    public event EventHandler<string>? SlotRemoved;
    public event EventHandler<ActiveSlotChangedEventArgs>? ActiveSlotChanged;
    public event EventHandler<SlotStateChangedEventArgs>? SlotStateChanged;
    public event EventHandler<SlotListChangedEventArgs>? SlotListChanged;

    public IReadOnlyDictionary<string, ModemSlot> Slots => _slots;
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

    public ModemPool(AsyncEventBus? eventBus = null)
    {
        _eventBus = eventBus ?? new AsyncEventBus();
    }

    /// <summary>
    /// Adds or updates a modem slot, attaching to the serial port.
    /// </summary>
    public async Task<ModemSlot> AddOrUpdateSlotAsync(
        string portName,
        int baudRate = 115200,
        string? name = null,
        string? slotId = null,
        string? proxyUrl = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);

        var id = slotId ?? portName.ToLowerInvariant();

        if (_slots.TryGetValue(id, out var existing))
        {
            if (existing.Modem != null)
                await existing.DetachHardwareAsync(ct).ConfigureAwait(false);
            existing.PortName = portName;
            existing.BaudRate = baudRate;
            if (!string.IsNullOrWhiteSpace(name)) existing.Name = name;
            if (proxyUrl != null) existing.ProxyUrl = proxyUrl;
            await existing.AttachAsync(ct).ConfigureAwait(false);
            try { SlotListChanged?.Invoke(this, new SlotListChangedEventArgs(_slots.Values.ToList())); } catch { }
            _eventBus.Publish("pool.slot.reattached", "ModemPool", existing.GetDiagnosticInfo());
            return existing;
        }

        var slot = new ModemSlot(id, portName, baudRate, name, proxyUrl, _eventBus);
        slot.StateChanged += (s, e) =>
        {
            try { SlotStateChanged?.Invoke(this, e); } catch { }
        };

        await slot.AttachAsync(ct).ConfigureAwait(false);

        _slots[id] = slot;

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
    /// Attaches an already constructed ModemSlot directly (useful for testing and mock topologies).
    /// </summary>
    public bool TryAddSlot(ModemSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (_slots.TryAdd(slot.Id, slot))
        {
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

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _slotLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Acquires an exclusive async lease on a slot for safe multi-operation execution.
    /// </summary>
    public async Task<IAsyncDisposable?> AcquireSlotLeaseAsync(string slotId, int timeoutMs = 5000, CancellationToken ct = default)
    {
        if (!_slots.TryGetValue(slotId, out _))
            return null;

        var sem = _slotLocks.GetOrAdd(slotId, _ => new SemaphoreSlim(1, 1));
        bool acquired = await sem.WaitAsync(timeoutMs, ct).ConfigureAwait(false);
        if (!acquired) return null;

        return new SlotLease(sem);
    }

    /// <summary>
    /// Removes and disposes a slot by ID with graceful draining of active leases.
    /// </summary>
    public async Task<bool> RemoveSlotAsync(string slotId, CancellationToken ct = default)
    {
        if (_slots.TryRemove(slotId, out var slot))
        {
            if (_slotLocks.TryGetValue(slotId, out var sem))
            {
                try
                {
                    await sem.WaitAsync(3000, ct).ConfigureAwait(false);
                    sem.Release();
                }
                catch { }
                _slotLocks.TryRemove(slotId, out _);
            }

            await slot.DisposeAsync().ConfigureAwait(false);

            lock (_lock)
            {
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

            try { SlotRemoved?.Invoke(this, slotId); } catch { }
            try { SlotListChanged?.Invoke(this, new SlotListChangedEventArgs(_slots.Values.ToList())); } catch { }
            _eventBus.Publish("pool.slot.removed", "ModemPool", slotId);
            return true;
        }

        return false;
    }

    private sealed class SlotLease(SemaphoreSlim semaphore) : IAsyncDisposable, IDisposable
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
            }
        }
    }

    /// <summary>
    /// Selects the primary active slot.
    /// </summary>
    public bool SelectSlot(string slotId)
    {
        if (_slots.TryGetValue(slotId, out var slot))
        {
            string? prevId;
            lock (_lock)
            {
                prevId = _activeSlotId;
                foreach (var s in _slots.Values)
                {
                    s.IsActive = false;
                }
                _activeSlotId = slot.Id;
                slot.IsActive = true;
            }

            try { ActiveSlotChanged?.Invoke(this, new ActiveSlotChangedEventArgs(prevId, slot.Id, slot)); } catch { }
            _eventBus.Publish("pool.slot.selected", "ModemPool", slotId);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Configures a dedicated SOCKS5 proxy URL for a specific slot.
    /// </summary>
    public bool SetSlotProxy(string slotId, string? proxyUrl)
    {
        if (_slots.TryGetValue(slotId, out var slot))
        {
            slot.ProxyUrl = proxyUrl;
            slot.VoWifi.ProxyUrl = proxyUrl;
            _eventBus.Publish("pool.slot.proxy_updated", "ModemPool", new { SlotId = slotId, ProxyUrl = proxyUrl ?? "direct" });
            return true;
        }

        return false;
    }

    public async Task<IReadOnlyList<ModemSlot>> DiscoverAndEnrichAsync(CancellationToken ct = default)
    {
        // SerialPort.Open and the first AT read can block synchronously before
        // their async wrappers yield.  Keep concurrent discovery requests from
        // fighting over ports, while each individual port probe runs on the
        // thread pool below.
        await _discoveryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
        string[] portNames;

        try
        {
            portNames = SerialPort.GetPortNames();
        }
        catch
        {
            portNames = Array.Empty<string>();
        }

        var tasks = portNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            // SerialPort's Windows driver can block a worker thread for the
            // entire probe timeout. LongRunning gives each candidate port a
            // dedicated thread so one dead Bluetooth/debug port cannot starve
            // the remaining probes in the thread pool.
            .Select(port => Task.Factory.StartNew(async () =>
        {
            // Skip ports already attached and online
            var existing = _slots.Values.FirstOrDefault(s => s.PortName.Equals(port, StringComparison.OrdinalIgnoreCase));
            if (existing != null && existing.State == SlotState.Online)
            {
                var responsive = false;
                try { responsive = existing.Modem?.IsOpen == true && await existing.Modem.PingAsync(ct).ConfigureAwait(false); }
                catch { }
                if (responsive)
                {
                    await existing.RefreshMetricsAsync(ct).ConfigureAwait(false);
                    return existing;
                }

                await existing.DetachHardwareAsync(ct).ConfigureAwait(false);
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                // A responsive modem needs additional time after the AT ping to
                // read SIM identity and arm CLIP/CNMI unsolicited notifications.
                // The old 1.5 s budget often cancelled this setup halfway through.
                cts.CancelAfter(15000);

                var slot = await AddOrUpdateSlotAsync(port, ct: cts.Token).ConfigureAwait(false);
                if (slot.State == SlotState.Online)
                {
                    return slot;
                }
                else
                {
                    await RemoveSlotAsync(slot.Id, ct).ConfigureAwait(false);
                }
            }
            catch
            {
                // Port was not a responsive AT modem or was busy
            }
            return null;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap());

        var probedSlots = await Task.WhenAll(tasks).ConfigureAwait(false);
        var validSlots = probedSlots.Where(s => s != null).ToList();

        // Deduplicate by IMEI or IMSI (some modems expose multiple AT-capable COM ports)
        var discovered = new List<ModemSlot>();
        var seenIdentities = new HashSet<string>();

        foreach (var slot in validSlots)
        {
            if (slot == null) continue;
            string identity = slot.Id;
            if (!string.IsNullOrWhiteSpace(slot.Imei)) identity = slot.Imei;
            else if (slot.Sim != null && !string.IsNullOrWhiteSpace(slot.Sim.Imsi)) identity = slot.Sim.Imsi;
            else if (slot.Sim != null && !string.IsNullOrWhiteSpace(slot.Sim.Iccid)) identity = slot.Sim.Iccid;

            if (seenIdentities.Add(identity))
            {
                discovered.Add(slot);
            }
            else
            {
                // It's a duplicate COM port for the same physical modem. Discard it.
                await RemoveSlotAsync(slot.Id, ct).ConfigureAwait(false);
            }
        }

        return discovered;
        }
        finally
        {
            _discoveryGate.Release();
        }
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

    public async ValueTask DisposeAsync()
    {
        foreach (var slot in _slots.Values)
        {
            try { await slot.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _slots.Clear();
        _discoveryGate.Dispose();
    }
}
