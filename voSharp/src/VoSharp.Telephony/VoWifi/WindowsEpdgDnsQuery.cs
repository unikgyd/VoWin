using System.Net;
using System.Runtime.InteropServices;

namespace VoSharp.Telephony.VoWifi;

/// <summary>Configured Windows DNS only. No custom/public server, global cache flush or subprocess.</summary>
internal static class WindowsEpdgDnsQuery
{
    internal const ulong QueryOptions = 0x8 | 0x800 | 0x1000; // BYPASS_CACHE, NO_MULTICAST, TREAT_AS_FQDN
    private const int Pending = 9506;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Request
    {
        public uint Version;
        public nint Name;
        public ushort Type;
        public ulong Options;
        public nint Servers;
        public uint InterfaceIndex;
        public nint Callback;
        public nint Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Result
    {
        public uint Version;
        public int Status;
        public ulong Options;
        public nint Records;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RecordHeader
    {
        public nint Next;
        public nint Name;
        public ushort Type;
        public ushort DataLength;
        public uint Flags;
        public uint Ttl;
        public uint Reserved;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void Completion(nint context, nint result);
    private static readonly Completion Callback = OnCompleted;

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern int DnsQueryEx(nint request, nint result, nint cancel);
    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern int DnsCancelQuery(nint cancel);
    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern void DnsRecordListFree(nint records, int freeType);

    internal static async Task<EpdgDnsFamilyResult> QueryAsync(string host, ushort type, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var query = new Query(host, type);
        try
        {
            query.Start();
            using var registration = ct.Register(query.Cancel);
            return await query.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally { query.Release(); }
    }

    private static void OnCompleted(nint context, nint result)
    {
        // The native reference keeps this GCHandle and all request buffers alive,
        // including after the caller timed out. Never let exceptions cross the ABI.
        ((Query)GCHandle.FromIntPtr(context).Target!).Complete(result);
    }

    private sealed class Query
    {
        internal readonly TaskCompletionSource<EpdgDnsFamilyResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ushort _type;
        private readonly nint _name, _request, _result, _cancel;
        private readonly GCHandle _self;
        private int _references = 2; // Caller + native completion, not thread-affine.
        private int _completed;
        internal Query(string host, ushort type)
        {
            _type = type;
            _name = Marshal.StringToHGlobalUni(host);
            _request = Marshal.AllocHGlobal(Marshal.SizeOf<Request>());
            _result = Marshal.AllocHGlobal(Marshal.SizeOf<Result>());
            _cancel = Marshal.AllocHGlobal(32);
            Marshal.Copy(new byte[32], 0, _cancel, 32);
            _self = GCHandle.Alloc(this);
            Marshal.StructureToPtr(new Request
            {
                Version = 1, Name = _name, Type = type, Options = QueryOptions,
                Callback = Marshal.GetFunctionPointerForDelegate(Callback), Context = GCHandle.ToIntPtr(_self)
            }, _request, false);
            Marshal.StructureToPtr(new Result { Version = 1 }, _result, false);
        }
        internal void Start()
        {
            try
            {
                var status = DnsQueryEx(_request, _result, _cancel);
                if (status != Pending) Complete(_result, status);
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref _completed, 1) == 0)
                {
                    Completion.TrySetException(ex);
                    Release();
                }
            }
        }
        internal void Cancel()
        {
            // CancellationTokenRegistration.Dispose waits for this call before the
            // caller releases its reference, so _cancel cannot be freed underneath us.
            if (Volatile.Read(ref _completed) == 0) _ = DnsCancelQuery(_cancel);
        }
        internal void Complete(nint resultPointer, int? synchronousStatus = null)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            nint records = 0;
            try
            {
                var result = Marshal.PtrToStructure<Result>(resultPointer);
                records = result.Records;
                var status = synchronousStatus ?? result.Status;
                var addresses = status == 0 ? ReadAddresses(records, _type) : [];
                Completion.TrySetResult(new(_type == 1 ? "A" : "AAAA", addresses,
                    status == 0 ? addresses.Length > 0 ? "resolved" : "no-data" : $"windows-dns-{status}"));
            }
            catch (Exception ex) { Completion.TrySetException(ex); }
            finally
            {
                if (records != 0) DnsRecordListFree(records, 1);
                Release();
            }
        }
        internal void Release()
        {
            if (Interlocked.Decrement(ref _references) != 0) return;
            _self.Free();
            Marshal.FreeHGlobal(_name);
            Marshal.FreeHGlobal(_request);
            Marshal.FreeHGlobal(_result);
            Marshal.FreeHGlobal(_cancel);
        }
    }

    internal static IPAddress[] ReadAddresses(nint record, ushort queryType)
    {
        var result = new List<IPAddress>();
        var visited = new HashSet<nint>();
        while (record != 0)
        {
            if (!visited.Add(record) || visited.Count > 1024) throw new InvalidDataException("Invalid DNS record list.");
            var header = Marshal.PtrToStructure<RecordHeader>(record);
            var length = queryType == 1 ? 4 : 16;
            if (header.Type == queryType && (header.Flags & 3) == 1 && header.DataLength >= length)
            {
                var bytes = new byte[length];
                Marshal.Copy(record + Marshal.SizeOf<RecordHeader>(), bytes, 0, bytes.Length);
                result.Add(new IPAddress(bytes));
            }
            record = header.Next;
        }
        return result.Distinct().ToArray();
    }
}
