using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using VoSharp.Ike;

namespace VoSharp.Telephony.VoWifi;

public interface IIkeRetryJournalStore
{
    Task<string?> LoadAsync(CancellationToken ct);
    Task<bool> CompareExchangeAsync(string? expected, string replacement, CancellationToken ct);
}

public sealed record IkeRetryClockStamp(string Boot, long Ticks);
public interface IIkeRetryClock { IkeRetryClockStamp Read(); }
public sealed record IkeRetryCard(string Key, string Device, string Epoch);
public sealed record IkeRetryTarget(string Host, string? Plmn, string Apn = "ims");
public sealed record IkeRetryStatus(string Source, string Scope, ushort? Cause, TimeSpan? Remaining, bool UntilCardEvent);
public enum IkeRetryFailure { Blocked, Store, Binding }
public sealed class IkeRetryGateException(IkeRetryFailure failure, string message, IkeRetryStatus? status = null, Exception? inner = null)
    : IkeAdmissionException(message, inner)
{
    public IkeRetryFailure Failure { get; } = failure;
    public IkeRetryStatus? Status { get; } = status;
}

/// <summary>
/// Private CAS journal. Begin is durable before IKE; an unconfirmed observation
/// leaves its active token unknown. Clock rebases never infer a modem power-off.
/// Caller must supply independently verified card lifecycle evidence to Prepare.
/// </summary>
public sealed class IkeRetryGate(IIkeRetryJournalStore store, IIkeRetryClock clock)
{
    public const int MaximumBytes = 1024 * 1024;
    private readonly IIkeRetryJournalStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IIkeRetryClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };
    private static readonly Regex HashPattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex TokenPattern = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);
    private static readonly Regex HostPattern = new("^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\.)*[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant);
    private static readonly Regex ApnPattern = new("^[a-z0-9](?:[a-z0-9.-]{0,251}[a-z0-9])?$", RegexOptions.CultureInvariant);

    private sealed class Journal
    {
        public required int Version { get; init; }
        public required Dictionary<string, string> Cards { get; init; }
        public required Dictionary<string, string> Devices { get; init; }
        public required Dictionary<string, Receipt> Entries { get; init; }
        public required Dictionary<string, Admission> Active { get; init; }
        public static Journal Empty() => new() { Version=1, Cards=[], Devices=[], Entries=[], Active=[] };
    }
    private sealed class Admission
    {
        public required string Epoch { get; init; }
        public required string Token { get; init; }
        public required IkeRetryTarget Target { get; init; }
    }
    private sealed class Receipt
    {
        public required string Card { get; init; }
        public required string Epoch { get; init; }
        public required string Scope { get; init; }
        public required string Host { get; init; }
        public string? Endpoint { get; init; }
        public required string? Plmn { get; init; }
        public required string Apn { get; init; }
        public required ushort Cause { get; init; }
        public required string Source { get; init; }
        public required bool UntilCardEvent { get; init; }
        public required long Delay { get; init; }
        public required string Boot { get; set; }
        public required long Deadline { get; set; }
    }

    public async Task<IkeRetryCard> PrepareVerifiedCardAsync(string deviceId, string iccid, CancellationToken ct = default, Func<bool>? stillCurrent = null)
    {
        if (stillCurrent?.Invoke() == false) throw Binding("The verified card read no longer owns the device lifecycle.");
        var card = CardKey(iccid); var device = Digest("device", Identity(deviceId, 256));
        return await ChangeAsync(j =>
        {
            // The journal read/CAS can wait. A removed or stopped owner must
            // not publish its earlier physical-card observation afterwards.
            if (stillCurrent?.Invoke() == false) throw Binding("The verified card read no longer owns the device lifecycle.");
            if (j.Devices.TryGetValue(device, out var previous) && previous != card)
            {
                foreach (var key in j.Entries.Where(e => e.Value.Card == previous).Select(e => e.Key).ToArray()) j.Entries.Remove(key);
                j.Active.Remove(previous);
                j.Cards[previous] = NewToken(); // Reject callbacks belonging to the old verified card epoch.
            }
            if (!j.Cards.TryGetValue(card, out var epoch)) j.Cards.Add(card, epoch = NewToken());
            j.Devices[device] = card;
            return new IkeRetryCard(card, device, epoch);
        }, ct).ConfigureAwait(false);
    }

    // Probe a cached identity before DNS/readiness without treating it as a
    // verified insertion event or creating/replacing a card epoch.
    public async Task InspectAsync(string iccid, IkeRetryTarget target, string? endpoint = null, CancellationToken ct = default)
    {
        target = Normalize(target); endpoint = endpoint is null ? null : NormalizeEndpoint(endpoint);
        await ChangeAsync(j => { ThrowIfBlocked(j, CardKey(iccid), target, null, endpoint); return true; }, ct).ConfigureAwait(false);
    }

    public Task<string> BeginAsync(IkeRetryCard card, IkeRetryTarget target, CancellationToken ct = default)
    {
        target = Normalize(target);
        return ChangeAsync(j =>
        {
            ValidateCard(j, card); ThrowIfBlocked(j, card.Key, target, null, null);
            var token = NewToken(); j.Active.Add(card.Key, new() { Epoch=card.Epoch, Token=token, Target=target });
            return token;
        }, ct);
    }

    public async Task PermitAsync(IkeRetryCard card, IkeRetryTarget target, string token, string? endpoint = null, CancellationToken ct = default)
    {
        target = Normalize(target); endpoint = endpoint is null ? null : NormalizeEndpoint(endpoint);
        await ChangeAsync(j =>
        {
            ValidateAttempt(j, card, target, token); ThrowIfBlocked(j, card.Key, target, token, endpoint); return true;
        }, ct).ConfigureAwait(false);
    }

    public async Task ObserveAsync(IkeRetryCard card, IkeRetryTarget target, string token, string endpoint,
        IkeNetworkRejection rejection, bool currentResponderAuthenticated, CancellationToken ct = default)
    {
        target = Normalize(target); endpoint = NormalizeEndpoint(endpoint);
        if (!currentResponderAuthenticated || rejection is null || Scope(rejection.NotifyType) != rejection.Scope)
            throw Binding("IKE rejection has no coherent current authenticated scope.");
        var scope = ScopeName(rejection.Scope);
        var until = rejection.Scope == IkeRejectionScope.Plmn || rejection.BackoffTimer.Status == IkeBackoffTimerStatus.Deactivated;
        var delay = until ? TimeSpan.Zero : rejection.BackoffTimer.Duration ?? TimeSpan.FromSeconds(rejection.Scope == IkeRejectionScope.Epdg ? 60 : 900);
        if (delay < TimeSpan.Zero || delay > TimeSpan.FromDays(365)) throw Binding("Invalid IKE rejection duration.");
        var source = until ? "card-event" : rejection.BackoffTimer.Status switch
        {
            IkeBackoffTimerStatus.Timed => "network-timer",
            IkeBackoffTimerStatus.Missing => "implementation-missing-timer",
            _ => "implementation-invalid-timer"
        };
        await ChangeAsync(j =>
        {
            ValidateAttempt(j, card, target, token);
            if (!until && delay == TimeSpan.Zero) return true; // Zero is not permission to erase stronger evidence.
            var now = Stamp();
            var receipt = new Receipt { Card=card.Key, Epoch=card.Epoch, Scope=scope, Host=target.Host,
                Endpoint=rejection.Scope==IkeRejectionScope.Epdg ? endpoint : null, Plmn=target.Plmn, Apn=target.Apn,
                Cause=rejection.NotifyType, Source=source, UntilCardEvent=until, Delay=delay.Ticks,
                Boot=now.Boot, Deadline=until ? 0 : checked(now.Ticks+delay.Ticks) };
            var key = ReceiptKey(receipt);
            if (!j.Entries.TryGetValue(key, out var old) || !old.UntilCardEvent && (until || old.Deadline < receipt.Deadline)) j.Entries[key] = receipt;
            return true;
        }, ct).ConfigureAwait(false);
    }

    public async Task CompleteAsync(IkeRetryCard card, string token, CancellationToken ct = default)
    {
        await ChangeAsync(j =>
        {
            ValidateCard(j, card);
            if (!j.Active.TryGetValue(card.Key, out var active) || active.Epoch != card.Epoch || active.Token != token)
                throw Binding("IKE completion no longer owns this attempt.");
            j.Active.Remove(card.Key); return true;
        }, ct).ConfigureAwait(false);
    }

    private async Task<T> ChangeAsync<T>(Func<Journal,T> change, CancellationToken ct)
    {
        for (var attempt=0; attempt<64; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            string? raw;
            try { raw = await _store.LoadAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) { throw StoreError(error); }
            var journal = Decode(raw); var now = Stamp();
            foreach (var pair in journal.Entries.ToArray())
            {
                var entry=pair.Value;
                if (entry.UntilCardEvent) continue;
                if (entry.Boot != now.Boot) { entry.Boot=now.Boot; entry.Deadline=checked(now.Ticks+entry.Delay); }
                else if (entry.Deadline <= now.Ticks) journal.Entries.Remove(pair.Key);
            }
            // A blocked result can still need a committed boot rebase. Commit
            // that transition first, then rethrow; never rebase only in RAM.
            T result=default!; IkeRetryGateException? blocked=null;
            try { result=change(journal); } catch (IkeRetryGateException error) when (error.Failure==IkeRetryFailure.Blocked) { blocked=error; }
            Validate(journal);
            var replacement=JsonSerializer.Serialize(journal,Json);
            if (Encoding.UTF8.GetByteCount(replacement)>MaximumBytes) throw StoreError(new InvalidDataException("IKE journal capacity reached."));
            if (raw==replacement) { if(blocked!=null)throw blocked; return result; }
            bool committed;
            try { committed=await _store.CompareExchangeAsync(raw,replacement,ct).ConfigureAwait(false); }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch(Exception error){throw StoreError(error);}
            if(committed){if(blocked!=null)throw blocked;return result;}
        }
        throw StoreError(new IOException("IKE journal changed too frequently."));
    }

    private void ThrowIfBlocked(Journal j,string card,IkeRetryTarget target,string? ownToken,string? peer)
    {
        if(j.Active.TryGetValue(card,out var active)&&active.Token!=ownToken)
            throw Blocked(new("uncertain-attempt","card",null,null,true));
        var now=Stamp(); Receipt? strongest=null;
        foreach(var e in j.Entries.Values.Where(e=>e.Card==card))
        {
            if(e.Plmn!=null&&target.Plmn!=null&&e.Plmn!=target.Plmn)continue;
            if(e.Scope!="plmn"&&e.Apn!=target.Apn)continue;
            if(e.Scope=="epdg"&&e.Host!=target.Host&&(peer==null||e.Endpoint!=null&&e.Endpoint!=peer))continue;
            if(strongest==null||!strongest.UntilCardEvent&&(e.UntilCardEvent||strongest.Deadline<e.Deadline))strongest=e;
        }
        if(strongest is { } entry)throw Blocked(new(entry.Source,entry.Scope,entry.Cause,
            entry.UntilCardEvent?null:TimeSpan.FromTicks(Math.Max(0,entry.Deadline-now.Ticks)),entry.UntilCardEvent));
    }

    private IkeRetryClockStamp Stamp()
    {
        try { var s=_clock.Read();if(s==null||!HashPattern.IsMatch(s.Boot)||s.Ticks<0)throw new InvalidDataException("Invalid IKE clock.");return s; }
        catch(Exception e){throw StoreError(e);}
    }
    private static void ValidateCard(Journal j,IkeRetryCard card)
    {
        if(card==null||!HashPattern.IsMatch(card.Key)||!HashPattern.IsMatch(card.Device)||!TokenPattern.IsMatch(card.Epoch)||
            !j.Cards.TryGetValue(card.Key,out var epoch)||epoch!=card.Epoch||!j.Devices.TryGetValue(card.Device,out var current)||current!=card.Key)
            throw Binding("The verified IKE card epoch is no longer current.");
    }
    private static void ValidateAttempt(Journal j,IkeRetryCard card,IkeRetryTarget target,string token)
    {
        ValidateCard(j,card);
        if(token==null||!TokenPattern.IsMatch(token)||!j.Active.TryGetValue(card.Key,out var a)||a.Epoch!=card.Epoch||a.Token!=token||a.Target!=target)
            throw Binding("The IKE attempt is not bound to this card and target.");
    }
    private static Journal Decode(string? raw)
    {
        if(raw==null)return Journal.Empty();
        try
        {
            if(Encoding.UTF8.GetByteCount(raw)>MaximumBytes)throw new InvalidDataException("Oversized IKE journal.");
            using var doc=JsonDocument.Parse(raw,new(){MaxDepth=16});
            var nodes=0;
            void Visit(JsonElement value)
            {
                if(++nodes>100000)throw new InvalidDataException("IKE journal complexity exceeded.");
                if(value.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var p in value.EnumerateObject()){if(!names.Add(p.Name))throw new InvalidDataException("Duplicate IKE field.");Visit(p.Value);}}
                else if(value.ValueKind==JsonValueKind.Array)foreach(var item in value.EnumerateArray())Visit(item);
            }
            Visit(doc.RootElement);
            var journal=JsonSerializer.Deserialize<Journal>(raw,Json)??throw new InvalidDataException("Missing IKE journal.");Validate(journal);return journal;
        }
        catch(IkeRetryGateException e)when(e.Failure!=IkeRetryFailure.Store){throw StoreError(e);}
        catch(Exception e)when(e is not IkeRetryGateException){throw StoreError(e);}
    }
    private static void Validate(Journal j)
    {
        try
        {
            if(j.Version!=1||j.Cards==null||j.Devices==null||j.Entries==null||j.Active==null||j.Cards.Count>256||j.Devices.Count>256||j.Entries.Count>2048||j.Active.Count>256)throw new InvalidDataException("Invalid IKE journal version/capacity.");
            foreach(var(c,e)in j.Cards)if(!HashPattern.IsMatch(c)||e==null||!TokenPattern.IsMatch(e))throw new InvalidDataException("Invalid IKE card.");
            foreach(var(d,c)in j.Devices)if(!HashPattern.IsMatch(d)||c==null||!j.Cards.ContainsKey(c))throw new InvalidDataException("Invalid IKE device.");
            foreach(var(c,a)in j.Active)if(a==null||!j.Cards.TryGetValue(c,out var e)||a.Epoch!=e||a.Token==null||!TokenPattern.IsMatch(a.Token)||Normalize(a.Target)!=a.Target)throw new InvalidDataException("Invalid IKE admission.");
            foreach(var(k,e)in j.Entries)
            {
                if(e==null||!j.Cards.TryGetValue(e.Card,out var epoch)||epoch!=e.Epoch||!HashPattern.IsMatch(e.Boot)||Normalize(new(e.Host,e.Plmn,e.Apn))!=new IkeRetryTarget(e.Host,e.Plmn,e.Apn)||
                    Scope(e.Cause)is not {} expected||e.Scope!=ScopeName(expected)||e.Source is not("card-event" or "network-timer" or "implementation-missing-timer" or "implementation-invalid-timer")||
                    e.Delay<0||e.Delay>TimeSpan.FromDays(365).Ticks||e.Scope=="plmn"&&!e.UntilCardEvent||e.UntilCardEvent!=(e.Source=="card-event")||e.UntilCardEvent&&(e.Delay!=0||e.Deadline!=0)||!e.UntilCardEvent&&(e.Delay==0||e.Deadline<e.Delay)||
                    e.Scope!="epdg"&&e.Endpoint!=null||e.Endpoint!=null&&NormalizeEndpoint(e.Endpoint)!=e.Endpoint||k!=ReceiptKey(e))throw new InvalidDataException("Invalid IKE receipt.");
            }
        }
        catch(IkeRetryGateException e)when(e.Failure!=IkeRetryFailure.Store){throw StoreError(e);}
        catch(Exception e)when(e is not IkeRetryGateException){throw StoreError(e);}
    }
    private static IkeRetryTarget Normalize(IkeRetryTarget target)
    {
        if(target==null)throw Binding("Missing IKE target.");
        var host=target.Host?.Trim().TrimEnd('.').ToLowerInvariant();
        if(host==null||host.Length>253||!HostPattern.IsMatch(host)&&!IPAddress.TryParse(host,out _))throw Binding("Invalid IKE host.");
        if(IPAddress.TryParse(host,out _))host=NormalizeEndpoint(host);
        var apn=target.Apn?.Trim().ToLowerInvariant();if(apn==null||!ApnPattern.IsMatch(apn))throw Binding("Invalid IKE APN.");
        if(!string.IsNullOrWhiteSpace(target.Plmn)&&!Regex.IsMatch(target.Plmn.Trim(),"^(?:[0-9]{5,6}|[0-9]{3}-[0-9]{2,3})$",RegexOptions.CultureInvariant))throw Binding("Invalid IKE PLMN.");
        var plmn=string.IsNullOrWhiteSpace(target.Plmn)?null:target.Plmn.Replace("-","").Trim();
        if(plmn!=null&&(plmn.Length is not(5 or 6)||plmn.Any(c=>c is <'0' or >'9')))throw Binding("Invalid IKE PLMN.");
        return new(host,plmn,apn);
    }
    private static string NormalizeEndpoint(string value)
    {
        if(!IPAddress.TryParse(value,out var ip))throw Binding("IKE peer must be a literal unicast address.");
        if(ip.AddressFamily==System.Net.Sockets.AddressFamily.InterNetworkV6&&ip.ScopeId!=0)throw Binding("Scoped IKE addresses are not supported.");
        if(ip.IsIPv4MappedToIPv6)ip=ip.MapToIPv4();
        var b=ip.GetAddressBytes();
        if(IPAddress.IsLoopback(ip)||ip.Equals(IPAddress.Any)||ip.Equals(IPAddress.IPv6Any)||ip.IsIPv6Multicast||ip.IsIPv6LinkLocal||
            b.Length==4&&(b[0]>=224||b[0]==0||b[0]==169&&b[1]==254))throw Binding("IKE peer must be a usable unicast address.");
        return ip.ToString();
    }
    private static string ReceiptKey(Receipt e)=>Digest("receipt",JsonSerializer.Serialize(new[]{e.Card,e.Epoch,e.Scope,e.Scope=="epdg"?e.Host:"",e.Endpoint??"",e.Plmn??"",e.Scope=="plmn"?"":e.Apn}));
    private static IkeRejectionScope? Scope(ushort cause)=>cause switch{10500=>IkeRejectionScope.Epdg,9002=>IkeRejectionScope.PlmnApn,9000 or 9001 or 9003 or 9006 or 11001 or 11011=>IkeRejectionScope.Plmn,_=>null};
    private static string ScopeName(IkeRejectionScope scope)=>scope switch{IkeRejectionScope.Epdg=>"epdg",IkeRejectionScope.PlmnApn=>"plmn-apn",_=>"plmn"};
    private static string CardKey(string iccid)=>Digest("card",Identity(iccid,128));
    private static string Identity(string value,int maximum)=>!string.IsNullOrWhiteSpace(value)&&value.Trim().Length<=maximum?value.Trim():throw Binding("Missing or oversized IKE identity.");
    private static string Digest(string purpose,string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("VoWin.IKE."+purpose+".v1:"+value))).ToLowerInvariant();
    private static string NewToken()=>Guid.NewGuid().ToString("N");
    private static IkeRetryGateException Binding(string message)=>new(IkeRetryFailure.Binding,message);
    private static IkeRetryGateException StoreError(Exception cause)=>new(IkeRetryFailure.Store,"IKE retry state cannot be confirmed; no new handshake is permitted.",inner:cause);
    private static IkeRetryGateException Blocked(IkeRetryStatus status)=>new(IkeRetryFailure.Blocked,"The current card's authenticated IKE retry restriction is still active.",status);
}

public sealed class MemoryIkeRetryJournalStore : IIkeRetryJournalStore
{
    private readonly object _sync=new();private string? _value;
    public Task<string?> LoadAsync(CancellationToken ct){ct.ThrowIfCancellationRequested();lock(_sync)return Task.FromResult(_value);}
    public Task<bool> CompareExchangeAsync(string? expected,string replacement,CancellationToken ct)
    {ct.ThrowIfCancellationRequested();lock(_sync){if(_value!=expected)return Task.FromResult(false);_value=replacement;return Task.FromResult(true);}}
}

/// <summary>An owner without a configured durable store must not silently enable IMS retry.</summary>
public sealed class UnavailableIkeRetryJournalStore : IIkeRetryJournalStore
{
    public Task<string?> LoadAsync(CancellationToken ct) => throw new IOException("A durable IKE retry store was not configured by the application owner.");
    public Task<bool> CompareExchangeAsync(string? expected,string replacement,CancellationToken ct) => throw new IOException("A durable IKE retry store was not configured by the application owner.");
}
