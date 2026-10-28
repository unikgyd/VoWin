using System.Text.Json;
using VoSharp.Ike;

namespace VoSharp.Ike.Tests;
public class SwuInitContractTests
{
    private static JsonDocument Contract() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"contracts","swu-ike-init-v1.json")));
    [Fact]
    public void SharedMessageFramingRejectsPartialChainsAndPreservesSkBoundary()
    {
        using var contract=Contract();
        foreach(var item in contract.RootElement.GetProperty("messages").EnumerateArray())
        {
            var bytes=Convert.FromHexString(item.GetProperty("hex").GetString()!);IkeWire.IkeMessage? parsed=null;
            var error=Record.Exception(()=>parsed=IkeWire.ParseMessage(bytes));
            Assert.True((error is null)==item.GetProperty("accepted").GetBoolean(),item.GetProperty("id").GetString());
            if(error is not null){Assert.IsType<IkeFormatException>(error);continue;}
            Assert.Equal(item.GetProperty("count").GetInt32(),parsed!.Payloads.Count);
            if(parsed.Payloads.Count>0)
            {
                Assert.Equal(item.GetProperty("firstKind").GetByte(),(byte)parsed.Payloads[0].Type);
                if(parsed.Payloads[0].Type is IkePayloadType.Encrypted or IkePayloadType.EncryptedFragment)
                    Assert.Equal(item.GetProperty("innerFirst").GetByte(),(byte)parsed.Payloads[0].NextPayloadOverride!.Value);
            }
            Assert.Equal(bytes,IkeWire.SerializeMessage(parsed));
        }
        Assert.Throws<IkeFormatException>(()=>IkeWire.SerializePayloadChain([new(IkePayloadType.Nonce,new byte[65532])]));
        Assert.Throws<IkeFormatException>(()=>IkeWire.SerializePayloadChain([new(IkePayloadType.Encrypted,[]),new(IkePayloadType.Nonce,[])]));
    }
    [Fact]
    public void SharedSaFramingHandlesTlvWithoutLosingUnsupportedAttributes()
    {
        using var contract=Contract();
        foreach(var item in contract.RootElement.GetProperty("proposals").EnumerateArray())
        {
            var bytes=Convert.FromHexString(item.GetProperty("hex").GetString()!);List<IkeProposal>? parsed=null;
            var error=Record.Exception(()=>parsed=IkeWire.DecodeProposals(bytes));
            Assert.True((error is null)==item.GetProperty("accepted").GetBoolean(),item.GetProperty("id").GetString());
            if(error is not null){Assert.IsType<IkeFormatException>(error);continue;}
            Assert.Equal(item.GetProperty("count").GetInt32(),parsed!.Count);var first=parsed[0].Transforms[0];
            var bits=item.GetProperty("firstKeyBits");Assert.Equal(bits.ValueKind==JsonValueKind.Null?(ushort?)null:bits.GetUInt16(),first.KeyLengthBits);
            Assert.Equal(item.GetProperty("unsupported").GetBoolean(),first.HasUnsupportedAttributes);
            Assert.Equal(item.GetProperty("selectable").GetBoolean(),Record.Exception(()=>IkeSuite.FromProposal(parsed[0])) is null);
        }
        var oversized=Enumerable.Range(1,64).Select(index=>new IkeProposal {ProposalNumber=(byte)index,Protocol=IkeProtocolId.Ike,Spi=new byte[255],
            Transforms=Enumerable.Range(0,64).Select(_=>new IkeTransform(1,12,128)).ToList()}).ToArray();
        Assert.Throws<IkeFormatException>(()=>IkeWire.EncodeProposals(oversized));
    }
    [Fact]
    public void SharedIndependentIkeKeySchedulesDoNotModifyCallerSecrets()
    {
        using var contract=Contract();
        foreach(var item in contract.RootElement.GetProperty("keys").EnumerateArray())
        {
            var suite=new IkeSuite(12,item.GetProperty("encryptionBits").GetInt32(),item.GetProperty("prf").GetUInt16(),item.GetProperty("integrity").GetUInt16(),2);
            var secret=Convert.FromHexString(item.GetProperty("shared").GetString()!);var unchanged=secret.ToArray();
            var keys=IkeCrypto.DeriveIkeKeys(suite,secret,Convert.FromHexString(item.GetProperty("ni").GetString()!),Convert.FromHexString(item.GetProperty("nr").GetString()!),0x0102030405060708,0x1112131415161718);
            var expected=item.GetProperty("keys").EnumerateArray().Select(value=>Convert.FromHexString(value.GetString()!)).ToArray();
            var actual=new[]{keys.SkD,keys.SkAi,keys.SkAr,keys.SkEi,keys.SkEr,keys.SkPi,keys.SkPr};
            for(int i=0;i<7;i++)Assert.Equal(expected[i],actual[i]);Assert.Equal(unchanged,secret);
            Assert.Throws<InvalidOperationException>(()=>IkeCrypto.PrfPlus(suite,secret,[],255*suite.PrfLength+1));
        }
    }
    [Fact]
    public void InitialNoncesCookieAndSingletonChecksFailBeforeAka()
    {
        var suite=IkeSuite.Preferred with {PrfId=7};
        foreach(int length in new[]{0,1,15,31,257}) Assert.Throws<IkeFormatException>(()=>IkeSession.ValidateInitNonce(new byte[length],suite));
        IkeSession.ValidateInitNonce(new byte[32],suite);
        using var modp=ModpGroup.Create2048();
        foreach(int length in new[]{1,255,257})Assert.Throws<IkeFormatException>(()=>modp.ComputeSharedSecret(new byte[length]));
        var message=new IkeWire.IkeMessage();message.Payloads.Add(new(IkePayloadType.Nonce,new byte[32]));
        Assert.NotNull(IkeSession.RequireSingleInitPayload(message,IkePayloadType.Nonce));
        message.Payloads.Add(new(IkePayloadType.Nonce,new byte[32]));Assert.Throws<IkeFormatException>(()=>IkeSession.RequireSingleInitPayload(message,IkePayloadType.Nonce));
        var cookie=new IkePayload(IkePayloadType.Notify,[0,0,0x40,0x06,1]);message.Payloads.Clear();message.Payloads.Add(cookie);
        Assert.NotNull(IkeSession.ReadInitCookie(message));message.Payloads.Add(cookie);Assert.Throws<IkeFormatException>(()=>IkeSession.ReadInitCookie(message));
        // RFC 7296 3.10 requires ignoring Protocol ID when the SPI is empty.
        message.Payloads.Clear();message.Payloads.Add(cookie with {Body=[1,0,0x40,0x06,1]});Assert.NotNull(IkeSession.ReadInitCookie(message));
    }
}
