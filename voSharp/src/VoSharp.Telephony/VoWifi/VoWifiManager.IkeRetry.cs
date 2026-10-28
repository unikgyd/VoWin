using System.Net;
using VoSharp.Ike;
using VoSharp.Sim;

namespace VoSharp.Telephony.VoWifi;

internal sealed record IkeBackendContext(EpdgResolvedEndpoint Endpoint,
    Func<IPAddress,CancellationToken,Task> Permit,
    Func<IkeNetworkRejection,IPAddress,CancellationToken,Task> Observe);

public partial class VoWifiManager
{
    private readonly IkeRetryGate _ikeRetry;
    private readonly string _ikeRetryDeviceId;
    private sealed record RetryPresentation(string? Iccid, IkeRetryStatus Status);
    private RetryPresentation? _ikeRetryPresentation;
    private bool _ikeRetryPaused;
    private bool _typedIkeFailure;
    public IkeRetryStatus? IkeRetryStatus => Volatile.Read(ref _ikeRetryPresentation)?.Status;
    /// <summary>
    /// Last confirmed observation for this exact card, not a live store read or
    /// permission to retry. Remaining is the value at that observation, not a countdown.
    /// </summary>
    public IkeRetryStatus? GetIkeRetryStatusFor(string? iccid)
    {
        var observation = Volatile.Read(ref _ikeRetryPresentation);
        return observation is not null && !string.IsNullOrWhiteSpace(iccid) && observation.Iccid == iccid ? observation.Status : null;
    }
    internal IkeRetryGate RetryGate => _ikeRetry;
    internal string RetryDeviceId => _ikeRetryDeviceId;
    internal Func<IkeBackendContext,IIkeBackend> IkeBackendFactory { get; set; } = context => new CSharpIkeBackend
    {
        AuthenticationHomePlmn = (context.Endpoint.HomeMcc,context.Endpoint.HomeMnc),
        ResponderAuthentication = new(context.Endpoint.Candidate.Host),
        BeforeInitialSend = context.Permit,
        ObserveNetworkRejection = context.Observe
    };

    private void ResetIkeRetryPresentation()
    {
        Volatile.Write(ref _ikeRetryPresentation,null);
        Volatile.Write(ref _ikeRetryPaused,false);
    }
    private void SetIkeRetryFailure(IkeRetryGateException error, string? iccid)
    {
        var status=error.Status ?? new(error.Failure==IkeRetryFailure.Store?"store-unavailable":"binding-unavailable","card",null,null,true);
        Volatile.Write(ref _ikeRetryPresentation,new(iccid, status));
        Volatile.Write(ref _ikeRetryPaused,status.UntilCardEvent);
    }
    private static string DescribeIkeRetryFailure(IkeRetryGateException error) => error.Status?.Remaining is { } remaining
        ? $"IKE carrier retry restriction: wait {Math.Max(0,(long)Math.Ceiling(remaining.TotalSeconds))} seconds; manual/network retries cannot bypass it."
        : "IKE retry is paused until the card lifecycle or uncertain local state is verified; no new handshake was sent.";

    private static IkeRetryTarget RetryTarget(EpdgCandidate candidate,string apn) => new(candidate.Host,candidate.Plmn,apn);

    private async Task<IReadOnlyList<EpdgCandidate>> FilterIkeCandidatesAsync(SimIdentity sim,EpdgDiscoveryOptions discovery,CancellationToken ct)
    {
        var candidates=EpdgResolver.BuildCandidates(sim,discovery);
        var allowed=new List<EpdgCandidate>();var blocked=new List<IkeRetryGateException>();
        foreach(var candidate in candidates)
        {
            try
            {
                var endpoint=IPAddress.TryParse(candidate.Host,out var ip)?ip.ToString():null;
                await _ikeRetry.InspectAsync(sim.Iccid,RetryTarget(candidate,discovery.IsEmergency?"sos":"ims"),endpoint,ct).ConfigureAwait(false);
                allowed.Add(candidate);
            }
            catch(IkeRetryGateException error)when(error.Failure==IkeRetryFailure.Blocked){blocked.Add(error);}
            catch(IkeRetryGateException error){SetIkeRetryFailure(error,sim.Iccid);throw;}
        }
        if(allowed.Count==0&&blocked.Count>0)
        {
            // A different permitted PLMN/APN remains usable. If every plan is
            // blocked, wait only until the earliest candidate can be admitted.
            var next=blocked.OrderBy(e=>e.Status?.Remaining??TimeSpan.MaxValue).First();
            SetIkeRetryFailure(next,sim.Iccid);throw next;
        }
        ResetIkeRetryPresentation();
        return allowed;
    }

    public async Task PreflightIkeRetryAsync(SimIdentity sim,string? customEpdg=null,CancellationToken ct=default)
    {
        var discovery=BuildEpdgDiscoveryOptions(customEpdg,ProxyUrl,null) with{LastSuccessfulSelection=_epdgHistory.Get(sim)};
        await FilterIkeCandidatesAsync(sim,discovery,ct).ConfigureAwait(false);
    }

    private async Task<EpdgResolvedEndpoint[]> FilterIkeEndpointsAsync(SimIdentity sim,IReadOnlyList<EpdgResolvedEndpoint> endpoints,string apn,CancellationToken ct)
    {
        var allowed=new List<EpdgResolvedEndpoint>();var blocked=new List<IkeRetryGateException>();
        foreach(var endpoint in endpoints)
        {
            try{await _ikeRetry.InspectAsync(sim.Iccid,RetryTarget(endpoint.Candidate,apn),endpoint.Address.ToString(),ct).ConfigureAwait(false);allowed.Add(endpoint);}
            catch(IkeRetryGateException error)when(error.Failure==IkeRetryFailure.Blocked){blocked.Add(error);}
            catch(IkeRetryGateException error){SetIkeRetryFailure(error,sim.Iccid);throw;}
        }
        if(allowed.Count==0&&blocked.Count>0)
        {
            var next=blocked.OrderBy(e=>e.Status?.Remaining??TimeSpan.MaxValue).First();SetIkeRetryFailure(next,sim.Iccid);throw next;
        }
        return allowed.ToArray();
    }

    private async Task<bool> WaitForIkeRetryAsync(CancellationToken ct)
    {
        while(_lastStartOptions is { } options&&KeepOnlineRequested)
        {
            try
            {
                var discovery=BuildEpdgDiscoveryOptions(options.CustomEpdg,options.ProxyUrl,options.LocalAddress) with{LastSuccessfulSelection=_epdgHistory.Get(options.Sim)};
                await FilterIkeCandidatesAsync(options.Sim,discovery,ct).ConfigureAwait(false);
                return true;
            }
            catch(IkeRetryGateException error)when(error.Failure==IkeRetryFailure.Blocked&&error.Status?.Remaining is { } remaining)
            {
                LastError=DescribeIkeRetryFailure(error);
                await _recoveryWake.WaitAsync(remaining>TimeSpan.Zero?remaining:TimeSpan.FromMilliseconds(1),ct).ConfigureAwait(false);
                // A wakeup is not permission. Re-read the durable gate before
                // touching AKA readiness, DNS or creating a new backend.
            }
            catch(IkeRetryGateException error)
            {
                SetIkeRetryFailure(error,options.Sim.Iccid);LastError=DescribeIkeRetryFailure(error);return false;
            }
        }
        return false;
    }

    private async Task<IkeBackendResult> StartGovernedIkeAsync(SimIdentity sim,EpdgResolvedEndpoint endpoint,string apn,
        Func<IIkeBackend,Task<IkeBackendResult>> start,Func<bool> isCurrent,CancellationToken ct)
    {
        // The core's hardware preflight has just verified this ICCID. Cached
        // identities and temporary reader errors never call PrepareVerified.
        var card=await _ikeRetry.PrepareVerifiedCardAsync(_ikeRetryDeviceId,sim.Iccid,ct,isCurrent).ConfigureAwait(false);
        if(!isCurrent())throw new IkeAdmissionException("IKE start intent was stopped before reserving an attempt.");
        var target=RetryTarget(endpoint.Candidate,apn);
        var token=await _ikeRetry.BeginAsync(card,target,ct).ConfigureAwait(false);
        var observationLock=new SemaphoreSlim(1,1);
        var observed=false;var closed=false;Exception? observationError=null;
        async Task Permit(IPAddress peer,CancellationToken send)
        {
            if(!isCurrent())throw new IkeAdmissionException("IKE start intent was stopped before sending.");
            await _ikeRetry.PermitAsync(card,target,token,peer.ToString(),send).ConfigureAwait(false);
            if(!isCurrent())throw new IkeAdmissionException("IKE start intent changed while checking its durable admission.");
        }
        async Task Observe(IkeNetworkRejection proof,IPAddress peer,CancellationToken unused)
        {
            using var save=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await observationLock.WaitAsync(save.Token).ConfigureAwait(false);
            try
            {
                if(closed)throw new IkeRetryGateException(IkeRetryFailure.Binding,"The IKE observer no longer owns this attempt.");
                observed=true;
                try{await _ikeRetry.ObserveAsync(card,target,token,peer.ToString(),proof,true,save.Token).ConfigureAwait(false);}
                catch(Exception error){observationError=error;throw;}
            }
            finally{observationLock.Release();}
        }
        _typedIkeFailure=true;
        IkeBackendResult result;
        try
        {
            _csharpIkeBackend?.Dispose();
            _csharpIkeBackend=IkeBackendFactory(new(endpoint,Permit,Observe)) ?? throw new InvalidOperationException("IKE backend factory returned no backend.");
            result=await start(_csharpIkeBackend).ConfigureAwait(false);
            // A backend implementation/test seam may return evidence directly.
            // The actual IKE engine commits synchronously before its cleanup.
            if(result.NetworkRejection!=null&&(result.InitialResponderAuthVerified||result.FinalAuthVerified)&&!observed)
                await Observe(result.NetworkRejection,endpoint.Address,CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await observationLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try{closed=true;}finally{observationLock.Release();}
            if(observationError!=null)
            {
                var uncertain=new IkeRetryGateException(IkeRetryFailure.Store,"IKE rejection could not be committed; its durable admission remains unknown.",new("uncertain-attempt","card",null,null,true),observationError);
                SetIkeRetryFailure(uncertain,sim.Iccid);throw uncertain;
            }
            using var finish=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _ikeRetry.CompleteAsync(card,token,finish.Token).ConfigureAwait(false);
        }
        if(!result.Success)
        {
            using var check=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _ikeRetry.InspectAsync(sim.Iccid,target,endpoint.Address.ToString(),check.Token).ConfigureAwait(false);
        }
        return result;
    }
}
