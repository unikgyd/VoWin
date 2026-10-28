using VoSharp.Kernel.SipGateway;
using VoSharp.Sip;
using System.Text.Json;

namespace VoSharp.Tests;

public sealed class SipGatewayResponseIdentityTests
{
    [Fact]
    public void SharedOpaqueResponseIdentityContract()
    {
        using var document=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"contracts","sip-response-identity-v1.json")));
        var root=document.RootElement;
        Assert.Equal(1,root.GetProperty("version").GetInt32());
        Assert.Equal(9,root.GetProperty("cases").GetArrayLength());
        foreach(var item in root.GetProperty("cases").EnumerateArray())
        {
            var request=Request();
            request.SetHeader("Call-ID",root.GetProperty("expected_call_id").GetString()!);
            request.SetHeader("From","<sip:1001@voice.test>;tag="+root.GetProperty("expected_from_tag").GetString());
            var response=request.CreateResponse(item.GetProperty("status").GetInt32(),"Test");
            response.SetHeader("Call-ID",item.GetProperty("call_id").GetString()!);
            var tag=item.GetProperty("from_tag").GetString();
            response.SetHeader("From","<sip:1001@voice.test>"+(string.IsNullOrEmpty(tag)?"":";tag="+tag));
            if(item.TryGetProperty("duplicate_from",out var duplicate)&&duplicate.GetBoolean())response.AddHeader("From",response.GetHeader("From")!);
            Assert.True(SipGateway.ResponseMatchesRequestIdentity(request,response)==item.GetProperty("accept").GetBoolean(),item.GetProperty("name").GetString());
        }
    }
    private static SipMessage Request()
    {
        var request = new SipMessage { IsRequest = true, Method = "MESSAGE", RequestUri = "sip:1002@lab.local" };
        request.SetHeader("Via", "SIP/2.0/UDP 127.0.0.1:15060;branch=z9hG4bK-test;rport");
        request.SetHeader("From", "\"Display ;tag=not-the-tag\" <sip:1001@lab.local>;tag=OpaqueTagA");
        request.SetHeader("To", "<sip:1002@lab.local>");
        request.SetHeader("Call-ID", "Opaque-Call-A"); request.SetHeader("CSeq", "1 MESSAGE");
        return request;
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("missing")]
    [InlineData("case")]
    [InlineData("duplicate")]
    [InlineData("duplicate-initial")]
    public void ResponseMustPreserveExistingDialogToTag(string kind)
    {
        var request = Request();
        if (kind != "duplicate-initial")
        {
            request.Method = "BYE";
            request.SetHeader("CSeq", "2 BYE");
            request.SetHeader("To", "<sip:1002@lab.local>;tag=RemoteTagA");
        }
        var response = request.CreateResponse(200, "OK");
        response.SetHeader("To", kind switch
        {
            "wrong" => "<sip:1002@lab.local>;tag=OtherTag",
            "missing" => "<sip:1002@lab.local>",
            "case" => "<sip:1002@lab.local>;tag=remotetaga",
            _ => "<sip:1002@lab.local>;tag=RemoteTagA;tag=RemoteTagA"
        });
        Assert.False(SipGateway.ResponseMatchesRequestIdentity(request, response));
    }

    [Fact]
    public void InitialResponseMayEstablishToTagAndDialogReplyMustKeepIt()
    {
        var request = Request();
        var response = request.CreateResponse(200, "OK");
        response.SetHeader("To", "<sip:1002@lab.local>;tag=NewRemoteTag");
        Assert.True(SipGateway.ResponseMatchesRequestIdentity(request, response));
        request.SetHeader("To", response.GetHeader("To")!);
        Assert.True(SipGateway.ResponseMatchesRequestIdentity(request, response));
    }

    [Fact]
    public void ValidCopiedIdentityAcceptsProvisionalAndFinalResponses()
    {
        var request = Request();
        Assert.True(SipGateway.ResponseMatchesRequestIdentity(request, request.CreateResponse(100, "Trying")));
        Assert.True(SipGateway.ResponseMatchesRequestIdentity(request, request.CreateResponse(200, "OK")));
    }

    [Theory]
    [InlineData("missing-tag")]
    [InlineData("wrong-tag")]
    [InlineData("tag-case")]
    [InlineData("duplicate-tag")]
    [InlineData("call-id-case")]
    [InlineData("duplicate-from")]
    [InlineData("invalid-status")]
    public void AmbiguousOrDifferentResponseIdentityIsRejected(string kind)
    {
        var request = Request(); var response = request.CreateResponse(200, "OK");
        switch (kind)
        {
            case "missing-tag": response.SetHeader("From", "<sip:1001@lab.local>"); break;
            case "wrong-tag": response.SetHeader("From", "<sip:1001@lab.local>;tag=other"); break;
            case "tag-case": response.SetHeader("From", "<sip:1001@lab.local>;tag=opaquetaga"); break;
            case "duplicate-tag": response.SetHeader("From", "<sip:1001@lab.local>;tag=OpaqueTagA;tag=OpaqueTagA"); break;
            case "call-id-case": response.SetHeader("Call-ID", "opaque-call-a"); break;
            case "duplicate-from": response.AddHeader("From", request.GetHeader("From")!); break;
            case "invalid-status": response.StatusCode = 900; break;
        }
        Assert.False(SipGateway.ResponseMatchesRequestIdentity(request, response));
    }
}
