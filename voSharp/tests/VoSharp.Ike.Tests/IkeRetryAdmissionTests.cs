using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using VoSharp.Common.Aka;
using VoSharp.Ike.Transport;

namespace VoSharp.Ike.Tests;

public class IkeRetryAdmissionTests
{
    private sealed class Sim:IAkaProvider
    {
        public int Authentications;
        public Task<bool> CheckReadyAsync(string? expectedIccid=null,CancellationToken ct=default)=>Task.FromResult(true);
        public Task<AkaResult> AuthenticateAsync(AkaChallenge challenge,CancellationToken ct=default){Authentications++;throw new InvalidOperationException("No AKA in a rejected transcript.");}
    }
    private static async Task<byte[]> Exact(Socket socket,int count,CancellationToken ct)
    {
        var bytes=new byte[count];var read=0;
        while(read<count){var n=await socket.ReceiveAsync(bytes.AsMemory(read),SocketFlags.None,ct);if(n==0)throw new IOException("Synthetic proxy closed early.");read+=n;}
        return bytes;
    }
    private static async Task Control(Socket listener,IPEndPoint relay,Task stop,CancellationToken ct)
    {
        using var socket=await listener.AcceptAsync(ct);
        var head=await Exact(socket,2,ct);Assert.Equal(5,head[0]);await Exact(socket,head[1],ct);
        await socket.SendAsync(new byte[]{5,0},SocketFlags.None,ct);
        var associate=await Exact(socket,10,ct);Assert.Equal(new byte[]{5,3,0,1,0,0,0,0,0,0},associate);
        await socket.SendAsync(new byte[]{5,0,0,1,127,0,0,1,(byte)(relay.Port>>8),(byte)relay.Port},SocketFlags.None,ct);
        await stop.WaitAsync(ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualDirectAndSocksRetransmissionIsDeniedBeforeSecondDatagram(bool socks)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var server=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);
        server.Bind(new IPEndPoint(IPAddress.Loopback,0));var relay=(IPEndPoint)server.LocalEndPoint!;
        using var listener=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback,0));listener.Listen(1);
        var stop=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var control=socks?Control(listener,relay,stop.Task,timeout.Token):Task.CompletedTask;
        using var proxy=socks?Socks5Client.TryParse("socks5://127.0.0.1:"+((IPEndPoint)listener.LocalEndPoint!).Port):null;
        var target=socks?IPAddress.Parse("192.0.2.9"):IPAddress.Loopback;var checks=0;
        try
        {
            using var transport=new IkeTransport(target,initialPort:socks?500:relay.Port,socks5Client:proxy,beforeInitialSend:(peer,_)=>
            {
                Assert.Equal(target,peer);checks++;
                if(checks>1)throw new IkeAdmissionException("synthetic durable denial",new SocketException((int)SocketError.HostUnreachable));
                return Task.CompletedTask;
            });
            var request=IkeWire.SerializeMessage(new(){InitiatorSpi=123,Exchange=IkeExchangeType.IkeSaInit,Flags=IkeFlags.Initiator});
            var attempt=transport.RoundTripAsync(request,timeout.Token);var buffer=new byte[2048];
            var received=await server.ReceiveFromAsync(buffer,SocketFlags.None,new IPEndPoint(IPAddress.Any,0),timeout.Token);
            if(socks){Assert.Equal(new byte[]{0,0,0,1,192,0,2,9,1,244},buffer[..10]);Assert.Equal(request,buffer[10..received.ReceivedBytes]);}
            else Assert.Equal(request,buffer[..received.ReceivedBytes]);
            await Assert.ThrowsAsync<IkeAdmissionException>(()=>attempt);Assert.Equal(2,checks);
            using var quiet=new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>{await server.ReceiveFromAsync(buffer,SocketFlags.None,new IPEndPoint(IPAddress.Any,0),quiet.Token);});
        }
        finally{stop.TrySetResult();await control;}
    }

    [Fact]
    public async Task ActualSessionPolicyFailureNeverBecomesNetworkOrProposalRetryProof()
    {
        using var server=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);server.Bind(new IPEndPoint(IPAddress.Loopback,0));
        var sim=new Sim();
        var result=await IkeSession.EstablishAsync(new(IPAddress.Loopback,sim,"234150123456789","234","15",EpdgPort:((IPEndPoint)server.LocalEndPoint!).Port,
            BeforeInitialSend:(_,_)=>throw new IkeAdmissionException("synthetic policy failure",new SocketException((int)SocketError.HostUnreachable))));
        Assert.False(result.Success);Assert.True(result.FailureCleanupComplete);Assert.Equal(0,sim.Authentications);
        Assert.False(result.RetryableTransportFailure);Assert.False(result.RetryableInitialTimeout);Assert.False(result.RetryableInitialProposal);Assert.False(result.RetryableAddressFamilyFailure);Assert.False(result.RetryableIdentityRejection);
        using var quiet=new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>{await server.ReceiveFromAsync(new byte[2048],SocketFlags.None,new IPEndPoint(IPAddress.Any,0),quiet.Token);});
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("cancel")]
    [InlineData("save-failure")]
    public async Task ActualSignedRejectionIsObservedBeforeDeleteEvenOnCancellationOrFailedCommit(string mode)
    {
        using var certificates=new ResponderCertificates();using var key=RSA.Create(2048);using var certificate=certificates.Leaf(key);
        using var socket=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);socket.Bind(new IPEndPoint(IPAddress.Loopback,0));
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));using var caller=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        // Reuse the unchanged trusted transcript from the already accepted
        // provider tests, not a new fake proof supplied to the manager.
        var serve=typeof(IkeBackoffProviderProofTests).GetMethod("Serve",BindingFlags.NonPublic|BindingFlags.Static)!;
        var server=(Task<bool>)serve.Invoke(null,new object[]{socket,"network",key,certificate.RawData,deadline.Token})!;
        var observations=0;var admissions=0;var committed=false;var sim=new Sim();
        var attempt=IkeSession.EstablishAsync(new(IPAddress.Loopback,sim,"234150123456789","234","15",
            EpdgPort:((IPEndPoint)socket.LocalEndPoint!).Port,Suite:IkeSuite.Preferred with{DhGroupId=IkeDhGroupId.Ecp256},
            ResponderAuthentication:new("epdg.example",[certificates.Root.RawData]),
            BeforeInitialSend:(_,_)=>{admissions++;Assert.False(committed);return Task.CompletedTask;},
            ObserveNetworkRejection:(proof,peer,save)=>
            {
                observations++;Assert.Equal(IPAddress.Loopback,peer);Assert.Equal((ushort)10500,proof.NotifyType);Assert.Equal(TimeSpan.FromMinutes(10),proof.BackoffTimer.Duration);
                Assert.False(server.IsCompleted);Assert.False(save.IsCancellationRequested);committed=true;
                if(mode=="cancel")caller.Cancel();
                if(mode=="save-failure")throw new IkeAdmissionException("synthetic commit failure");
                return Task.CompletedTask;
            }),caller.Token);
        if(mode=="cancel")await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>attempt);
        else
        {
            var result=await attempt;Assert.False(result.Success);Assert.NotNull(result.NetworkRejection);Assert.True(result.InitialResponderAuthVerified);Assert.True(result.FailureCleanupComplete);
            Assert.False(result.RetryableTransportFailure);Assert.False(result.RetryableAddressFamilyFailure);Assert.False(result.RetryableIdentityRejection);
        }
        Assert.True(await server);Assert.Equal(1,observations);Assert.Equal(1,admissions);Assert.Equal(0,sim.Authentications);
    }
}
