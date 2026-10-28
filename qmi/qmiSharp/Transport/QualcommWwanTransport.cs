using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using qmiSharp.Core;

namespace qmiSharp.Transport;

/// <summary>
/// Qualcomm/Quectel qcusbwwan private control-file transport. The driver owns
/// CTL and allocates a service file/CID via GET_SERVICE_FILE. Each service file
/// exchanges only the QMUX service body; this class restores the outer header
/// expected by qmiSharp. No driver hook, USB reconfiguration or AT bridge.
/// </summary>
public sealed class QualcommWwanTransport : IDriverManagedQmiTransport
{
    private const string NetworkClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
    private const string DosPrefix = @"\DosDevices\Global\";
    private const string Win32Prefix = @"\\.\Global\";
    // CTL_CODE(FILE_DEVICE_UNKNOWN, function, METHOD_BUFFERED, FILE_ANY_ACCESS)
    private const uint IoctlGetServiceFile = (0x22u << 16) | (3340u << 2);
    private const uint IoctlGetClientId = (0x22u << 16) | (3343u << 2);
    private const uint IoctlQmiReady = (0x22u << 16) | (2100u << 2);

    private sealed class ServiceFile(SafeFileHandle handle, FileStream stream, ushort service,
        byte clientId, CancellationTokenSource stop)
    {
        public SafeFileHandle Handle { get; } = handle;
        public FileStream Stream { get; } = stream;
        public ushort Service { get; } = service;
        public byte ClientId { get; } = clientId;
        public CancellationTokenSource Stop { get; } = stop;
        public SemaphoreSlim WriteGate { get; } = new(1, 1);
        public Task ReadTask { get; set; } = Task.CompletedTask;
    }

    private readonly SafeFileHandle _control;
    private readonly string _controlLeaf;
    private readonly Dictionary<(ushort Service, byte ClientId), ServiceFile> _services = [];
    private readonly object _servicesLock = new();
    private readonly SemaphoreSlim _allocationGate = new(1, 1);
    private readonly SemaphoreSlim _receiveGate = new(1, 1);
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private byte[]? _current;
    private int _currentOffset;
    private bool _disposed;

    private static void Trace(string value)
    {
        if (Environment.GetEnvironmentVariable("VOWIN_QMI_TRACE") == "1")
            Console.Error.WriteLine("[qcusbwwan] " + value);
    }

    public string Name { get; }
    public bool IsConnected => !_disposed && !_control.IsClosed && !_control.IsInvalid;

    public QualcommWwanTransport(string controlPath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("qcusbwwan control files require Windows.");
        if (!TryParseControlPath(controlPath, out _controlLeaf))
            throw new ArgumentException("Not a validated qcusbwwan control-file path.", nameof(controlPath));
        Name = $"QualcommWwan({controlPath})";
        _control = File.OpenHandle(controlPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            DeviceIoControlChecked(_control, IoctlQmiReady, [], [], "QMI_READY");
        }
        catch
        {
            _control.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Auto-bind only when one Quectel qcusbwwan adapter publishes a private
    /// control file. Multiple adapters require explicit selection and later
    /// QMI/AT IMEI verification; never guess a port-to-modem pairing.
    /// </summary>
    public static string? FindSingleControlPath()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var networkClass = Registry.LocalMachine.OpenSubKey(NetworkClassKey);
            if (networkClass is null) return null;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var keyName in networkClass.GetSubKeyNames())
            {
                try
                {
                    using var adapter = networkClass.OpenSubKey(keyName);
                    if (adapter?.GetValue("MatchingDeviceId") is not string hardwareId ||
                        !hardwareId.StartsWith(@"USB\VID_2C7C&", StringComparison.OrdinalIgnoreCase) ||
                        adapter.GetValue("DriverDesc") is not string description ||
                        !description.Contains("Quectel", StringComparison.OrdinalIgnoreCase) ||
                        adapter.GetValue("QCDeviceControlFile") is not string dosName ||
                        adapter.GetValue("NetCfgInstanceId") is not string interfaceId ||
                        !dosName.StartsWith(DosPrefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var leaf = dosName[DosPrefix.Length..];
                    if (!leaf.StartsWith(interfaceId, StringComparison.OrdinalIgnoreCase) ||
                        leaf.Length <= interfaceId.Length + 1 || leaf[interfaceId.Length] != '_' ||
                        !int.TryParse(leaf[(interfaceId.Length + 1)..], NumberStyles.None,
                            CultureInfo.InvariantCulture, out var index) || index <= 0)
                        continue;
                    var path = Win32Prefix + leaf;
                    if (TryParseControlPath(path, out _)) paths.Add(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                  System.Security.SecurityException)
                {
                    // Other network adapters may have protected registry keys.
                }
            }
            return paths.Count == 1 ? paths.Single() : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                        System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>Bind the QMI control file to the selected AT port's physical USB container.</summary>
    public static string? FindControlPathForAtPort(string atPort)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return SelectControlPathForAtPort(WindowsUsbTopology.EnumeratePresent(), atPort); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null; // Missing/ambiguous topology must fall back to AT, never another modem's QMI file.
        }
    }

    internal static string? SelectControlPathForAtPort(IEnumerable<WindowsUsbFunction> functions, string atPort)
    {
        var inventory = functions.ToArray();
        var port = WindowsUsbTopology.SelectPort(inventory, atPort);
        var matches = inventory.Where(f => f.ContainerId == port.ContainerId &&
            f.ClassId == WindowsUsbTopology.NetworkClass &&
            f.InstanceId.StartsWith(@"USB\VID_2C7C&", StringComparison.OrdinalIgnoreCase) &&
            f.Name.Contains("Quectel", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) return null;
        var network = matches[0];
        if (network.QmiControlFile is not { } dosName || network.NetworkInterfaceId is not { } interfaceId ||
            !Guid.TryParseExact(interfaceId, "B", out _) ||
            !dosName.StartsWith(DosPrefix + interfaceId + "_", StringComparison.OrdinalIgnoreCase)) return null;
        var path = Win32Prefix + dosName[DosPrefix.Length..];
        return TryParseControlPath(path, out _) ? path : null;
    }

    internal static bool TryParseControlPath(string? path, out string leaf)
    {
        leaf = string.Empty;
        if (path is null || !path.StartsWith(Win32Prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var value = path[Win32Prefix.Length..];
        var separator = value.LastIndexOf('_');
        if (separator <= 0 || separator == value.Length - 1 ||
            !Guid.TryParseExact(value[..separator], "B", out _) ||
            !int.TryParse(value[(separator + 1)..], NumberStyles.None,
                CultureInfo.InvariantCulture, out var index) || index <= 0)
            return false;
        leaf = value;
        return true;
    }

    public async ValueTask<byte> AllocateClientIdAsync(ushort service,
        CancellationToken cancellationToken = default)
    {
        if (service is 0 or > byte.MaxValue)
            throw new NotSupportedException("qcusbwwan exposes per-service QMI files, not CTL or QRTR.");
        await _allocationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            byte[] nameBytes = new byte[512];
            int size = DeviceIoControlChecked(_control, IoctlGetServiceFile,
                [(byte)service], nameBytes, "GET_SERVICE_FILE");
            Trace($"GET_SERVICE_FILE service={service}, bytes={size}");
            var childName = Encoding.ASCII.GetString(nameBytes, 0, size).TrimEnd('\0');
            var expected = _controlLeaf + "\\" + service.ToString(CultureInfo.InvariantCulture);
            if (!childName.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new IOException("qcusbwwan returned an unexpected service-file name.");
            cancellationToken.ThrowIfCancellationRequested();
            var handle = File.OpenHandle(Win32Prefix + childName, FileMode.Open,
                FileAccess.ReadWrite, FileShare.ReadWrite, FileOptions.Asynchronous);
            FileStream? stream = null;
            try
            {
                byte[] cidBytes = new byte[1];
                if (DeviceIoControlChecked(handle, IoctlGetClientId, [], cidBytes,
                    "QMI_GET_CLIENT_ID") != 1 || cidBytes[0] == 0)
                    throw new IOException("qcusbwwan did not assign a QMI client ID.");
                var cid = cidBytes[0];
                Trace($"QMI_GET_CLIENT_ID service={service}, cid={cid}");
                // A pending read must not hold FileStream's buffered async I/O
                // gate while a QMI request is written on the same device file.
                stream = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: true);
                var file = new ServiceFile(handle, stream, service, cid, new CancellationTokenSource());
                lock (_servicesLock)
                {
                    if (_services.ContainsKey((service, cid)))
                        throw new IOException("qcusbwwan reused an active QMI client ID.");
                    _services.Add((service, cid), file);
                }
                file.ReadTask = Task.Run(() => ReadServiceAsync(file));
                return cid;
            }
            catch
            {
                if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
                handle.Dispose();
                throw;
            }
        }
        finally { _allocationGate.Release(); }
    }

    public async ValueTask ReleaseClientIdAsync(ushort service, byte clientId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ServiceFile? file;
        lock (_servicesLock)
        {
            if (!_services.Remove((service, clientId), out file)) return;
        }
        file.Stop.Cancel();
        await file.Stream.DisposeAsync().ConfigureAwait(false);
        file.Handle.Dispose();
        try { await file.ReadTask.ConfigureAwait(false); } catch { }
        file.Stop.Dispose();
        file.WriteGate.Dispose();
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var packet = QmiPacket.Unmarshal(buffer.Span);
        if (packet.ServiceType is 0 or > byte.MaxValue || buffer.Length < QmuxHeader.Size)
            throw new NotSupportedException("qcusbwwan service files do not accept QMI CTL/QRTR frames.");
        ServiceFile file;
        lock (_servicesLock)
        {
            if (!_services.TryGetValue((packet.ServiceType, packet.ClientId), out file!))
                throw new InvalidOperationException("No qcusbwwan service file owns this QMI CID.");
        }
        await file.WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Trace($"send service={packet.ServiceType}, cid={packet.ClientId}, message=0x{packet.MessageId:X4}");
            await file.Stream.WriteAsync(buffer[QmuxHeader.Size..], cancellationToken)
                .ConfigureAwait(false);
            await file.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            Trace("send flushed");
        }
        finally { file.WriteGate.Release(); }
    }

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return 0;
        await _receiveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _current ??= await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            int count = Math.Min(buffer.Length, _current.Length - _currentOffset);
            _current.AsMemory(_currentOffset, count).CopyTo(buffer);
            _currentOffset += count;
            if (_currentOffset == _current.Length)
            {
                _current = null;
                _currentOffset = 0;
            }
            return count;
        }
        finally { _receiveGate.Release(); }
    }

    private async Task ReadServiceAsync(ServiceFile file)
    {
        byte[] body = new byte[ushort.MaxValue - 5];
        try
        {
            while (!file.Stop.IsCancellationRequested)
            {
                int size = await file.Stream.ReadAsync(body, file.Stop.Token).ConfigureAwait(false);
                if (size == 0) throw new IOException("qcusbwwan service file closed unexpectedly.");
                Trace($"receive service={file.Service}, cid={file.ClientId}, bytes={size}");
                var frame = WrapServiceBody(file.Service, file.ClientId, body.AsSpan(0, size));
                await _incoming.Writer.WriteAsync(frame, file.Stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (file.Stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (file.Stop.IsCancellationRequested) { }
        catch (Exception exception) when (!_disposed && !file.Stop.IsCancellationRequested)
        {
            _incoming.Writer.TryComplete(exception);
        }
    }

    internal static byte[] WrapServiceBody(ushort service, byte cid, ReadOnlySpan<byte> body)
    {
        if (service is 0 or > byte.MaxValue || cid == 0 ||
            body.Length < 7 || body.Length > ushort.MaxValue - 5)
            throw new FormatException("Invalid qcusbwwan service response.");
        var frame = new byte[QmuxHeader.Size + body.Length];
        frame[0] = QmuxHeader.ExpectedIFType;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1, 2), (ushort)(body.Length + 5));
        frame[3] = 0x80;
        frame[4] = (byte)service;
        frame[5] = cid;
        body.CopyTo(frame.AsSpan(QmuxHeader.Size));
        return frame;
    }

    private static int DeviceIoControlChecked(SafeFileHandle handle, uint code,
        byte[] input, byte[] output, string name)
    {
        if (!DeviceIoControl(handle, code, input, input.Length, output, output.Length,
            out int returned, IntPtr.Zero))
            throw new IOException($"qcusbwwan {name} failed (Win32 {Marshal.GetLastWin32Error()}).");
        return returned;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code,
        byte[] input, int inputLength, byte[] output, int outputLength,
        out int returned, IntPtr overlapped);

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        ServiceFile[] files;
        lock (_servicesLock)
        {
            files = _services.Values.ToArray();
            _services.Clear();
        }
        foreach (var file in files) file.Stop.Cancel();
        foreach (var file in files)
        {
            await file.Stream.DisposeAsync().ConfigureAwait(false);
            file.Handle.Dispose();
            try { await file.ReadTask.ConfigureAwait(false); } catch { }
            file.Stop.Dispose();
            file.WriteGate.Dispose();
        }
        _control.Dispose();
        _incoming.Writer.TryComplete();
        _allocationGate.Dispose();
        _receiveGate.Dispose();
    }
}
