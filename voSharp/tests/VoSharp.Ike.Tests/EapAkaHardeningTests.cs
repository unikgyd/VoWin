using System.Buffers.Binary;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VoSharp.Common.Aka;

namespace VoSharp.Ike.Tests;
public class EapAkaHardeningTests
{
    private sealed class Sim : IAkaProvider
    {
        public int Calls, ReadyChecks; public bool Synchronize, Block, ChangeCard;
        public byte[] Res = Convert.FromHexString("1122334455667788"), Ck = Enumerable.Repeat((byte)0xcc, 16).ToArray(), Ik = Enumerable.Repeat((byte)0xee, 16).ToArray();
        public Task<bool> CheckReadyAsync(string? expectedIccid = null, CancellationToken ct = default) => Task.FromResult(!ChangeCard || ++ReadyChecks < 2);
        public async Task<AkaResult> AuthenticateAsync(AkaChallenge challenge, CancellationToken ct = default)
        {
            var call = Calls++; if (Block) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            if (Synchronize && call == 0) return AkaResult.SyncFailed(Enumerable.Repeat((byte)0x42, 14).ToArray());
            return AkaResult.Succeeded(Res, Ck, Ik);
        }
    }
    private static JsonDocument Contract() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"contracts","eap-aka-v1.json")));
    private static byte[] Hex(JsonElement value,string field) => Convert.FromHexString(value.GetProperty(field).GetString()!);
    private static EapAkaClient Client(JsonElement value, Sim sim) => new(sim,value.GetProperty("imsi").GetString()!,"460","00",selectedEapMethod:value.GetProperty("method").GetByte(),expectedNetworkName:"WLAN");
    private static List<byte[]> Attributes(JsonElement value) => EapAkaClient.ParseAkaAttributes(Hex(value,"request").AsSpan(8)).Where(a=>a.Type!=11).Select(a=>a.Raw).ToList();
    private static byte[] Signed(JsonElement value,byte id,byte subtype,List<byte[]> attributes)
    {
        var data = new List<byte>{subtype,0,0}; foreach(var a in attributes)data.AddRange(a);
        data.AddRange(EapAkaClient.MarshalAkaAttribute(11,new byte[18]));
        var packet=EapAkaClient.MarshalEapPacket(new(1,id,value.GetProperty("method").GetByte(),data.ToArray()));
        var mac=value.GetProperty("method").GetByte()==23?HMACSHA1.HashData(Hex(value,"k_aut"),packet):HMACSHA256.HashData(Hex(value,"k_aut"),packet);
        mac.AsSpan(0,16).CopyTo(packet.AsSpan(packet.Length-16)); return packet;
    }
    [Fact]
    public void SharedKeySchedulesAndIndependentFipsKnownAnswer()
    {
        using var c=Contract();var fips=c.RootElement.GetProperty("fips");Assert.Equal(Hex(fips,"output"),EapAkaClient.Fips1862Prf(Hex(fips,"seed"),40));
        Assert.Throws<ArgumentException>(()=>EapAkaClient.Fips1862Prf(new byte[19],40));Assert.Throws<ArgumentException>(()=>EapAkaClient.Fips1862Prf(new byte[20],4097));
        foreach(var v in c.RootElement.GetProperty("keys").EnumerateArray().Concat(c.RootElement.GetProperty("flows").EnumerateArray()))
        {
            var identity=Encoding.UTF8.GetBytes(v.GetProperty("identity").GetString()!);var ik=Hex(v,"ik");var ck=Hex(v,"ck");var copy=ik.ToArray();
            var keys=v.GetProperty("method").GetByte()==23?EapAkaClient.DeriveAkaKeys(identity,ik,ck):EapAkaClient.DeriveAkaPrimeKeys(identity,ik,ck,Hex(v,"autn"),Encoding.UTF8.GetBytes(v.GetProperty("network").GetString()!));
            Assert.Equal(Hex(v,"k_encr"),keys.KEncr);Assert.Equal(Hex(v,"k_aut"),keys.KAut);Assert.Equal(Hex(v,"msk"),keys.Msk);Assert.Equal(Hex(v,"emsk"),keys.Emsk);Assert.Equal(copy,ik);
        }
    }
    [Fact]
    public async Task SharedFullFlowsCacheExactResponsesAndProtectFinalSuccess()
    {
        using var c=Contract();foreach(var v in c.RootElement.GetProperty("flows").EnumerateArray())
        {
            var sim=new Sim();using var client=Client(v,sim);var request=Hex(v,"request");
            Assert.Equal(Hex(v,"response"),(await client.HandleAsync(request)).Response);
            Assert.Equal(Hex(v,"response"),(await client.HandleAsync(request)).Response);Assert.Equal(1,sim.Calls);
            if(v.GetProperty("protectedResult").GetBoolean())Assert.Equal(Hex(v,"notificationResponse"),(await client.HandleAsync(Hex(v,"notificationRequest"))).Response);
            Assert.True((await client.HandleAsync(Hex(v,"success"))).IsSuccess);Assert.Equal(Hex(v,"msk"),client.Keys!.Msk);
            Assert.Equal(Enumerable.Repeat((byte)0xcc,16),sim.Ck);var keys=client.Keys;client.Dispose();Assert.All(keys!.Msk,b=>Assert.Equal(0,b));
        }
    }
    [Fact]
    public async Task DuplicateUnknownMandatoryAndMalformedPrimeAttributesNeverReachSim()
    {
        using var c=Contract();foreach(var v in c.RootElement.GetProperty("flows").EnumerateArray())for(var variant=0;variant<6;variant++)
        {
            var sim=new Sim();using var client=Client(v,sim);var a=Attributes(v);var request=Hex(v,"request");
            switch(variant)
            {
                case 0: request[4]=(byte)(request[4]==23?50:23);break;
                case 1: a.Add(a[0].ToArray());request=Signed(v,40,1,a);break;
                case 2: a.Add(EapAkaClient.MarshalAkaAttribute(7,[0,0]));request=Signed(v,40,1,a);break;
                case 3: a[0]=a[0][..16];a[0][1]=4;request=Signed(v,40,1,a);break;
                case 4: request=[3,40,0,4];break;
                default: a.Add(EapAkaClient.MarshalAkaAttribute(129,new byte[18]));request=Signed(v,40,1,a);break;
            }
            var error=await Record.ExceptionAsync(()=>client.HandleAsync(request));Assert.True(error is AuthenticationException or IkeFormatException);Assert.Equal(0,sim.Calls);Assert.Null(client.Keys);
        }
        var prime=c.RootElement.GetProperty("flows")[2];for(var variant=0;variant<5;variant++)
        {
            var sim=new Sim();using var client=Client(prime,sim);var a=Attributes(prime);
            switch(variant)
            {
                case 0:a.First(a=>a[0]==2)[10]&=0x7f;break;
                case 1:a.RemoveAll(a=>a[0]==24);break;
                case 2:a.Add(EapAkaClient.MarshalAkaAttribute(24,[0,1]));break;
                case 3:Encoding.ASCII.GetBytes("EVIL").CopyTo(a.First(a=>a[0]==23),4);break;
                default:BinaryPrimitives.WriteUInt16BigEndian(a.First(a=>a[0]==23).AsSpan(2),100);break;
            }
            await Assert.ThrowsAnyAsync<AuthenticationException>(()=>client.HandleAsync(Signed(prime,40,1,a)));Assert.Equal(0,sim.Calls);
        }
    }
    [Fact]
    public async Task InvalidMacSuccessIdentifierAndUnprotectedResultClearKeys()
    {
        using var c=Contract();var v=c.RootElement.GetProperty("flows")[1];for(var variant=0;variant<4;variant++)
        {
            var sim=new Sim();using var client=Client(v,sim);var request=Hex(v,"request");
            if(variant==0){request[^1]^=1;await Assert.ThrowsAnyAsync<AuthenticationException>(()=>client.HandleAsync(request));}
            else{await client.HandleAsync(request);var success=Hex(v,"success");success[1]=(byte)(variant==1?41:40);
                if(variant==3){success=request.ToArray();success[1]=41;}
                await Assert.ThrowsAnyAsync<AuthenticationException>(()=>client.HandleAsync(success));}
            Assert.Null(client.Keys);Assert.Equal(1,sim.Calls);
        }
    }
    [Fact]
    public async Task KdfNegotiationAndSyncPreserveExactOfferWithoutDuplicateSimOperations()
    {
        using var c=Contract();var v=c.RootElement.GetProperty("flows")[2];var sim=new Sim{Synchronize=true};using var client=Client(v,sim);
        var a=Attributes(v);a.RemoveAll(a=>a[0]==24);a.Add(EapAkaClient.MarshalAkaAttribute(24,[0,2]));a.Add(EapAkaClient.MarshalAkaAttribute(24,[0,1]));
        var first=Signed(v,40,1,a);var answer=(await client.HandleAsync(first)).Response!;Assert.Equal(new byte[]{24,1,0,1},answer[8..]);
        Assert.Equal(answer,(await client.HandleAsync(first)).Response);Assert.Equal(0,sim.Calls);
        a.Insert(0,EapAkaClient.MarshalAkaAttribute(24,[0,1]));var challenge=Signed(v,41,1,a);var auts=(await client.HandleAsync(challenge)).Response!;
        Assert.Equal(4,auts[5]);var attributes=EapAkaClient.ParseAkaAttributes(auts.AsSpan(8));Assert.Equal(Enumerable.Repeat((byte)0x42,14),attributes[0].Raw[2..]);
        Assert.Equal(new byte[]{1,2,1},attributes.Where(a=>a.Type==24).Select(a=>a.Raw[3]));
        Assert.Equal(auts,(await client.HandleAsync(challenge)).Response);Assert.Equal(1,sim.Calls);
        a.First(a=>a[0]==1)[4]^=1;Assert.Equal(1,(await client.HandleAsync(Signed(v,42,1,a))).Response![5]);Assert.Equal(2,sim.Calls);
    }
    [Fact]
    public async Task CancellationAndCardChangeInvalidateRunAndNeverRetrySim()
    {
        using var c=Contract();var v=c.RootElement.GetProperty("flows")[0];var sim=new Sim{Block=true};using var client=Client(v,sim);var request=Hex(v,"request");
        using var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(25));await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>client.HandleAsync(request,cancel.Token));
        await Assert.ThrowsAnyAsync<AuthenticationException>(()=>client.HandleAsync(request));Assert.Equal(1,sim.Calls);
        sim=new Sim{ChangeCard=true};using var changed=Client(v,sim);await Assert.ThrowsAnyAsync<AuthenticationException>(()=>changed.HandleAsync(request));Assert.Null(changed.Keys);Assert.Equal(1,sim.Calls);
    }
    [Fact]
    public async Task IdentityCheckcodeIncludesReservedBytesAndRetransmissionsOnlyOnce()
    {
        using var c=Contract();foreach(var v in c.RootElement.GetProperty("flows").EnumerateArray().Take(3))for(var invalid=0;invalid<3;invalid++)
        {
            var sim=new Sim();using var client=Client(v,sim);var data=new List<byte>{5,0x55,0xa7};
            data.AddRange(EapAkaClient.MarshalAkaAttribute(10,[0x34,0x12]));
            if(invalid==2)data.AddRange(EapAkaClient.MarshalAkaAttribute(200,[7,8]));
            var identityRequest=EapAkaClient.MarshalEapPacket(new(1,3,v.GetProperty("method").GetByte(),data.ToArray()));
            var identityResponse=(await client.HandleAsync(identityRequest)).Response!;Assert.Equal(identityResponse,(await client.HandleAsync(identityRequest)).Response);
            var transcript=identityRequest.Concat(identityResponse).ToArray();var check=v.GetProperty("method").GetByte()==23?SHA1.HashData(transcript):SHA256.HashData(transcript);
            var a=Attributes(v);if(invalid!=2){if(invalid==1)check[0]^=1;a.Add(EapAkaClient.MarshalAkaAttribute(134,new byte[2].Concat(check).ToArray()));}
            var operation=client.HandleAsync(Signed(v,40,1,a));
            if(invalid==0){Assert.NotNull((await operation).Response);Assert.Equal(1,sim.Calls);}else{await Assert.ThrowsAnyAsync<AuthenticationException>(()=>operation);Assert.Equal(0,sim.Calls);}
        }
    }
    [Fact]
    public async Task EncryptedAttributesAreValidatedAfterMacAndNeverPermitNestedProtection()
    {
        using var c=Contract();var v=c.RootElement.GetProperty("flows")[0];
        foreach(var innerKind in new byte[]{132,11,129,130,135,1})
        {
            var sim=new Sim();using var client=Client(v,sim);byte[] inner;
            if(innerKind==132)inner=EapAkaClient.MarshalAkaAttribute(132,[0,3,(byte)'a',(byte)'b',(byte)'c']);
            else inner=EapAkaClient.MarshalAkaAttribute(innerKind,innerKind==135?new byte[2]:new byte[18]);
            var padding=16-inner.Length%16;if(padding!=16)inner=inner.Concat(EapAkaClient.MarshalAkaAttribute(6,new byte[padding-2])).ToArray();
            var iv=Enumerable.Repeat((byte)0x21,16).ToArray();using var aes=Aes.Create();aes.Key=Hex(v,"k_encr");
            var encrypted=aes.EncryptCbc(inner,iv,PaddingMode.None);var a=Attributes(v);
            a.Add(EapAkaClient.MarshalAkaAttribute(129,new byte[2].Concat(iv).ToArray()));a.Add(EapAkaClient.MarshalAkaAttribute(130,new byte[2].Concat(encrypted).ToArray()));
            var operation=client.HandleAsync(Signed(v,40,1,a));if(innerKind==132)Assert.Equal(Hex(v,"response"),(await operation).Response);
            else await Assert.ThrowsAnyAsync<AuthenticationException>(()=>operation);
            Assert.Equal(1,sim.Calls);
        }
    }
    [Fact]
    public async Task LegacyBiddingDownRequiresFreshPrimeSessionAndCodecBoundsAreStrict()
    {
        using var c=Contract();var v=c.RootElement.GetProperty("flows")[0];var sim=new Sim();using var client=Client(v,sim);var a=Attributes(v);a.Add(EapAkaClient.MarshalAkaAttribute(136,[0x80,0]));
        await Assert.ThrowsAsync<EapMethodMismatchException>(()=>client.HandleAsync(Signed(v,40,1,a)));Assert.Equal(0,sim.Calls);
        Assert.Throws<IkeFormatException>(()=>EapAkaClient.ParseEapPacket([0,1,0,4]));
        Assert.Throws<IkeFormatException>(()=>EapAkaClient.MarshalEapPacket(new(1,1,23,new byte[8192])));
        Assert.Throws<IkeFormatException>(()=>EapAkaClient.ParseAkaAttributes([10,1,0,0,10,1,0,0]));
        Assert.True(EapAkaValidation.NetworkMatches("WLAN","WLAN:operator"));Assert.False(EapAkaValidation.NetworkMatches("WLAN","WLAN-EVIL"));
        Assert.Throws<ArgumentException>(()=>EapAkaClient.BuildPermanentIdentity("460001234567890","abc","00"));
    }
}
