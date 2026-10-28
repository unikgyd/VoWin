using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VoSharp.HostIms;

// Windows SDK ras.h uses pshpack4.h, including on x64. ABI checked by Print-RasAbi.cpp.
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
internal struct RasDevice
{
    public uint Size;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] public string Type;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] public string Name;
}
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
internal struct RasConnection
{
    public uint Size;
    public IntPtr Handle;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Entry;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] public string DeviceType;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] public string DeviceName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Phonebook;
    public uint SubEntry;
    public Guid EntryId;
    public uint Flags;
    public ulong Luid;
    public Guid CorrelationId;
}
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct RasTunnelEndpoint { public uint Type; public ulong AddressLow, AddressHigh; }
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
internal struct RasStatus
{
    public uint Size, State, Error;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] public string DeviceType;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] public string DeviceName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] public string PhoneNumber;
    public RasTunnelEndpoint Local, Remote;
    public uint SubState;
}
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
internal struct RasDialParameters
{
    public uint Size;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Entry;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] public string PhoneNumber;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] public string CallbackNumber;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string UserName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Password;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string Domain;
    public uint SubEntry;
    public UIntPtr CallbackId;
    public uint InterfaceIndex;
    public IntPtr EncryptedPassword;
}

internal static class WindowsRas
{
    internal const string EntryName = "VoWin IMS PPP Probe";
    internal const uint Connected = 0x2000, Disconnected = 0x2001;
    internal const int EntryBytes = 6724, Options2Offset = 4048, Ipv6MetricOffset = 5652;
    private const uint BufferTooSmall = 603, InvalidHandle = 6;
    private delegate uint EnumRas(IntPtr buffer, ref uint bytes, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void DialCallback(IntPtr connection, uint message, uint state, uint error, uint extendedError);

    private static T[] Enumerate<T>(EnumRas enumerate) where T : struct
    {
        uint bytes = 0;
        var result = enumerate(IntPtr.Zero, ref bytes, out var count);
        if (result == 0 && count == 0) return [];
        if (result != BufferTooSmall) Check(result);
        var stride = Marshal.SizeOf<T>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (bytes < stride || bytes > 16 * 1024 * 1024) throw new InvalidDataException("Invalid RAS enumeration size.");
            var allocated = bytes;
            var buffer = Marshal.AllocHGlobal((int)allocated);
            try
            {
                Marshal.WriteInt32(buffer, stride);
                result = enumerate(buffer, ref bytes, out count);
                if (result == BufferTooSmall) continue;
                Check(result);
                if ((ulong)count * (uint)stride > allocated) throw new InvalidDataException("Invalid RAS enumeration count.");
                return Enumerable.Range(0, (int)count).Select(i => Marshal.PtrToStructure<T>(buffer + i * stride)).ToArray();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new IOException("RAS device list changed repeatedly.");
    }

    internal static RasDevice[] Devices() => Enumerate<RasDevice>(RasEnumDevicesW);
    internal static RasConnection[] Connections() => Enumerate<RasConnection>(RasEnumConnectionsW);
    internal static bool HasImsConnection(string? entryName = null) => Connections().Any(connection =>
        entryName != null ? connection.Entry == entryName : connection.Entry == EntryName ||
            System.Text.RegularExpressions.Regex.IsMatch(connection.Entry, "^VoWin IMS [0-9a-f]{16}$"));
    internal static RasDevice SelectModem(IEnumerable<RasDevice> devices)
    {
        var matches = devices.Where(device => device.Type == "modem" && device.Name.Contains("Quectel USB Modem Port", StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidOperationException($"Expected exactly one Quectel RAS modem; found {matches.Length}. No dial attempted.");
    }

    internal static byte[] BuildEntry(RasDevice modem)
    {
        var buffer = new byte[EntryBytes];
        void Number(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), value);
        void Text(int offset, int capacity, string value)
        {
            if (value.Length >= capacity) throw new ArgumentException("RAS field exceeds the SDK limit.");
            Encoding.Unicode.GetBytes(value).CopyTo(buffer, offset);
        }
        Number(0, EntryBytes); Number(324, 4 | 8); Number(328, 1); // RASNP_Ip | IPv6, RASFP_Ppp; CCP off.
        Number(3496, 1); // RASET_Phone
        Number(Options2Offset, 0x20000); Number(Ipv6MetricOffset, 9999);
        Text(38, 129, "*99***5#"); Text(1892, 17, modem.Type); Text(1926, 129, modem.Name);
        return buffer;
    }

    internal static string CreatePhonebook(RasDevice modem, string parent, string entryName = EntryName)
    {
        var directory = Path.Combine(parent, "ras-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "ims.pbk");
        try
        {
            var entry = BuildEntry(modem);
            Check(RasSetEntryPropertiesW(path, entryName, entry, (uint)entry.Length, IntPtr.Zero, 0));
            return path;
        }
        catch { RemovePhonebook(path); throw; }
    }
    internal static void RemovePhonebook(string path)
    {
        // Only the one newly created file/directory. Never recursively remove temporary trees.
        File.Delete(path);
        Directory.Delete(Path.GetDirectoryName(path)!);
    }
    internal static uint ReadPhonebookMetric(string path, string entryName = EntryName)
    {
        var buffer = new byte[EntryBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, EntryBytes);
        uint bytes = EntryBytes, deviceBytes = 0;
        Check(RasGetEntryPropertiesW(path, entryName, buffer, ref bytes, IntPtr.Zero, ref deviceBytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(Ipv6MetricOffset, 4));
    }

    internal static async Task<int> RunWorker(int maxSeconds, string? atPort = null, string entryName = EntryName, Guid? expectedContainer = null)
    {
        if (maxSeconds is < 15 or > 3600 || !Console.IsInputRedirected)
            throw new ArgumentException("Managed RAS requires private redirected stdin and a 15-3600 second lease.");
        if (entryName != EntryName && !System.Text.RegularExpressions.Regex.IsMatch(entryName, "^VoWin IMS [0-9a-f]{16}$"))
            throw new ArgumentException("Invalid controlled RAS entry name.");
        if (HasImsConnection(entryName)) throw new InvalidOperationException("This modem already has an IMS RAS connection; refusing to adopt it.");
        RasDevice modem;
        if (atPort == null) modem = SelectModem(Devices()); // Legacy diagnostic protocol only.
        else
        {
            ModemControl.ValidatePort(atPort);
            var binding = WindowsModemBinding.Resolve(atPort);
            if (expectedContainer == null || binding.ContainerId != expectedContainer)
                throw new InvalidOperationException("USB modem identity changed before RAS dial.");
            modem = binding.SelectRasDevice(Devices());
        }
        RecoveryGuard.EnsureSecureRoot();
        var path = CreatePhonebook(modem, RecoveryGuard.Root, entryName);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DialCallback callback = (_, _, state, error, extended) =>
        {
            // Never propagate a managed exception through a native callback.
            try
            {
                Console.WriteLine($"RAS state={state} error={error} extended={extended}");
                if (error != 0) completion.TrySetException(Error(error));
                else if (state == Connected) completion.TrySetResult();
                else if (state == Disconnected) completion.TrySetException(new IOException("RAS disconnected before readiness."));
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        };
        IntPtr handle = IntPtr.Zero;
        var disconnected = false;
        try
        {
            var parameters = new RasDialParameters { Size = (uint)Marshal.SizeOf<RasDialParameters>(), Entry = entryName, PhoneNumber = "*99***5#" };
            Check(RasDialW(IntPtr.Zero, path, ref parameters, 1, callback, out handle));
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(90));
            Console.WriteLine($"RAS connected; holding until STOP/owner exit (maximum {maxSeconds} seconds).");
            await MonitorLeaseAsync(Console.In, TimeSpan.FromSeconds(maxSeconds), () =>
            {
                var status = new RasStatus { Size = (uint)Marshal.SizeOf<RasStatus>() };
                return RasGetConnectStatusW(handle, ref status) == 0 && status.State == Connected && status.Error == 0;
            });
            return 0;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                var result = RasHangUpW(handle);
                if (result != InvalidHandle) Check(result);
                var timer = Stopwatch.StartNew();
                while (timer.Elapsed < TimeSpan.FromSeconds(60))
                {
                    var status = new RasStatus { Size = (uint)Marshal.SizeOf<RasStatus>() };
                    if (RasGetConnectStatusW(handle, ref status) == InvalidHandle) { disconnected = true; break; }
                    await Task.Delay(250);
                }
            }
            else disconnected = true;
            GC.KeepAlive(callback);
            if (!disconnected) throw new IOException("RAS hangup not confirmed; phonebook retained and independent recovery remains armed.");
            RemovePhonebook(path);
        }
    }

    internal static async Task MonitorLeaseAsync(TextReader input, TimeSpan lease, Func<bool> isConnected)
    {
        var timer = Stopwatch.StartNew();
        // Console.In's SyncTextReader may block before returning a Task. Isolate it from the lease timer.
        Task<string?> Read() => Task.Run(async () => await input.ReadLineAsync());
        var pending = Read();
        while (timer.Elapsed < lease)
        {
            if (!isConnected()) throw new IOException("RAS lease lost its connection.");
            if (await Task.WhenAny(pending, Task.Delay(250)) == pending)
            {
                var command = await pending;
                if (command is null or "STOP") return;
                if (command != "RENEW") throw new InvalidDataException("Invalid RAS lease command.");
                timer.Restart(); pending = Read();
            }
        }
        throw new TimeoutException("RAS lease heartbeat expired.");
    }

    private static Exception Error(uint error)
    {
        var text = new StringBuilder(512);
        return RasGetErrorStringW(error, text, text.Capacity) == 0
            ? new Win32Exception((int)error, $"RAS {error}: {text}") : new Win32Exception((int)error);
    }
    private static void Check(uint result) { if (result != 0) throw Error(result); }
    [DllImport("rasapi32.dll", ExactSpelling = true)] private static extern uint RasEnumDevicesW(IntPtr buffer, ref uint bytes, out uint count);
    [DllImport("rasapi32.dll", ExactSpelling = true)] private static extern uint RasEnumConnectionsW(IntPtr buffer, ref uint bytes, out uint count);
    [DllImport("rasapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern uint RasSetEntryPropertiesW(string phonebook, string entry, byte[] data, uint bytes, IntPtr device, uint deviceBytes);
    [DllImport("rasapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern uint RasGetEntryPropertiesW(string phonebook, string entry, [In, Out] byte[] data, ref uint bytes, IntPtr device, ref uint deviceBytes);
    [DllImport("rasapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern uint RasDialW(IntPtr extensions, string phonebook, ref RasDialParameters parameters, uint notifierType, DialCallback notifier, out IntPtr handle);
    [DllImport("rasapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern uint RasGetConnectStatusW(IntPtr handle, ref RasStatus status);
    [DllImport("rasapi32.dll", ExactSpelling = true)] private static extern uint RasHangUpW(IntPtr handle);
    [DllImport("rasapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern uint RasGetErrorStringW(uint code, StringBuilder text, int characters);
}
