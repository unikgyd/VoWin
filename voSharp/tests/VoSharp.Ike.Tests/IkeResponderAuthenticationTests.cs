using System.Formats.Asn1;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VoSharp.Ike.Tests;

public sealed class ResponderCertificates : IDisposable
{
    internal RSA RootKey { get; } = RSA.Create(2048);
    internal X509Certificate2 Root { get; }
    internal DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
    public ResponderCertificates()
    {
        var request = new CertificateRequest("CN=VoWin synthetic test root", RootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        Root = request.CreateSelfSigned(Now.AddDays(-30), Now.AddDays(30));
    }
    internal X509Certificate2 Leaf(AsymmetricAlgorithm key, string mode = "valid")
    {
        var request = key is RSA rsa ? new CertificateRequest("CN=untrusted-CN.example", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            : new CertificateRequest("CN=untrusted-CN.example", (ECDsa)key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(mode == "non-signing" ? X509KeyUsageFlags.KeyEncipherment : X509KeyUsageFlags.DigitalSignature, true));
        if (mode != "no-eku") request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        { new(mode == "tls-only" ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.17") }, false));
        if (mode != "cn-only") { var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("epdg.example"); request.CertificateExtensions.Add(san.Build()); }
        return request.Create(Root.SubjectName, X509SignatureGenerator.CreateForRSA(RootKey, RSASignaturePadding.Pkcs1),
            Now.AddDays(-2), mode == "expired" ? Now.AddDays(-1) : Now.AddDays(2), RandomNumberGenerator.GetBytes(16));
    }
    public void Dispose() { Root.Dispose(); RootKey.Dispose(); }
}

public class IkeResponderAuthenticationTests : IClassFixture<ResponderCertificates>
{
    private readonly ResponderCertificates _certificates;
    public IkeResponderAuthenticationTests(ResponderCertificates certificates) => _certificates = certificates;
    internal static byte[] GenericAlgorithm(string oid, bool pss = false)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER); writer.PushSequence(); writer.WriteObjectIdentifier(oid);
        if (!pss) writer.WriteNull();
        else
        {
            writer.PushSequence();
            var t0 = new Asn1Tag(TagClass.ContextSpecific, 0, true); writer.PushSequence(t0);
            writer.PushSequence(); writer.WriteObjectIdentifier("2.16.840.1.101.3.4.2.1"); writer.WriteNull(); writer.PopSequence(); writer.PopSequence(t0);
            var t1 = new Asn1Tag(TagClass.ContextSpecific, 1, true); writer.PushSequence(t1);
            writer.PushSequence(); writer.WriteObjectIdentifier("1.2.840.113549.1.1.8");
            writer.PushSequence(); writer.WriteObjectIdentifier("2.16.840.1.101.3.4.2.1"); writer.WriteNull(); writer.PopSequence(); writer.PopSequence(); writer.PopSequence(t1);
            var t2 = new Asn1Tag(TagClass.ContextSpecific, 2, true); writer.PushSequence(t2); writer.WriteInteger(32); writer.PopSequence(t2);
            writer.PopSequence();
        }
        writer.PopSequence(); return writer.Encode();
    }

    [Theory]
    [InlineData("rsa")]
    [InlineData("generic-rsa")]
    [InlineData("pss")]
    [InlineData("pss-bad-params")]
    [InlineData("root-snapshot")]
    [InlineData("p256")]
    [InlineData("p384")]
    [InlineData("p521")]
    [InlineData("generic-ecdsa")]
    [InlineData("no-eku")]
    [InlineData("irrelevant-certificate")]
    [InlineData("irrelevant-encoding")]
    [InlineData("irrelevant-empty-encoding")]
    [InlineData("wrong-host")]
    [InlineData("cn-only")]
    [InlineData("no-root")]
    [InlineData("expired")]
    [InlineData("tls-only")]
    [InlineData("non-signing")]
    [InlineData("bad-signature")]
    [InlineData("duplicate-auth")]
    [InlineData("bare-der")]
    [InlineData("not-negotiated")]
    [InlineData("wrong-curve")]
    public void RealCertificatesAndSignaturesAreValidated(string mode)
    {
        using AsymmetricAlgorithm key = mode is "p256" or "generic-ecdsa" or "wrong-curve" ? ECDsa.Create(ECCurve.NamedCurves.nistP256)
            : mode == "p384" ? ECDsa.Create(ECCurve.NamedCurves.nistP384)
            : mode == "p521" ? ECDsa.Create(ECCurve.NamedCurves.nistP521) : RSA.Create(2048);
        using var leaf = _certificates.Leaf(key, mode);
        var initial = new byte[] { 1, 2, 3 }; var nonce = new byte[32]; var skpr = Enumerable.Repeat((byte)7, 32).ToArray();
        var id = new IkePayload(IkePayloadType.IdentificationResponder, [2, 0, 0, 0, (byte)'i', (byte)'m', (byte)'s']);
        var idHash = HMACSHA256.HashData(skpr, id.Body); byte[] signed = [.. initial, .. nonce, .. idHash];
        var generic = mode is "generic-rsa" or "pss" or "pss-bad-params" or "generic-ecdsa" or "bare-der" or "not-negotiated";
        byte method = generic ? (byte)14 : key is RSA ? (byte)1 : mode == "p384" || mode == "wrong-curve" ? (byte)10 : mode == "p521" ? (byte)11 : (byte)9;
        var hash = method == 1 ? HashAlgorithmName.SHA1 : method == 10 ? HashAlgorithmName.SHA384 : method == 11 ? HashAlgorithmName.SHA512 : HashAlgorithmName.SHA256;
        var signature = key is RSA rsa ? rsa.SignData(signed, hash, mode.StartsWith("pss", StringComparison.Ordinal) ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1)
            : ((ECDsa)key).SignData(signed, hash, generic ? DSASignatureFormat.Rfc3279DerSequence : DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (generic)
        {
            var pss = mode.StartsWith("pss", StringComparison.Ordinal);
            var oid = pss ? "1.2.840.113549.1.1.10" : key is RSA ? "1.2.840.113549.1.1.11" : "1.2.840.10045.4.3.2";
            var algorithm = GenericAlgorithm(oid, pss);
            if (mode == "pss-bad-params") algorithm[^1] = 31;
            signature = mode == "bare-der" ? [.. algorithm, .. signature] : [(byte)algorithm.Length, .. algorithm, .. signature];
        }
        if (mode == "bad-signature") signature[^1] ^= 1;
        var auth = new IkePayload(IkePayloadType.Authentication, [method, 0, 0, 0, .. signature]);
        var payloads = new List<IkePayload> { id, auth, new(IkePayloadType.Certificate, [4, .. leaf.RawData]) };
        if (mode == "duplicate-auth") payloads.Add(auth);
        if (mode == "irrelevant-certificate") payloads.Add(new(IkePayloadType.Certificate, [4, .. _certificates.Root.RawData]));
        if (mode == "irrelevant-encoding") payloads.Add(new(IkePayloadType.Certificate, [255, 0]));
        if (mode == "irrelevant-empty-encoding") payloads.Add(new(IkePayloadType.Certificate, [255]));
        var rootBytes = _certificates.Root.RawData;
        var policy = new IkeResponderAuthenticationOptions(mode == "wrong-host" ? "other.example" : "epdg.example",
            mode == "no-root" ? [] : [rootBytes]);
        if (mode == "root-snapshot") Array.Fill(rootBytes, (byte)0);
        bool valid = mode is "rsa" or "generic-rsa" or "pss" or "p256" or "p384" or "p521" or "generic-ecdsa" or "no-eku" or "irrelevant-certificate" or "irrelevant-encoding" or "irrelevant-empty-encoding" or "root-snapshot";
        if (valid) Assert.True(IkeResponderAuthentication.Verify(payloads, initial, nonce, IkeSuite.Preferred with { PrfId = IkePrfId.HmacSha2_256 }, skpr, policy, true));
        else Assert.Throws<AuthenticationException>(() => IkeResponderAuthentication.Verify(payloads, initial, nonce,
            IkeSuite.Preferred with { PrfId = IkePrfId.HmacSha2_256 }, skpr, policy, mode != "not-negotiated"));
    }

    [Fact]
    public void SignatureHashNotifyIgnoresProtocolButNotSpiOrMalformedLength()
    {
        Assert.True(IkeResponderAuthentication.ReadSignatureHashSupport([new(IkePayloadType.Notify, [255, 0, 0x40, 0x2f, 0, 2])]));
        foreach (byte[] body in new byte[][] { [0, 1, 0x40, 0x2f, 0, 2], [0, 0, 0x40, 0x2f], [0, 0, 0x40, 0x2f, 2] })
            Assert.Throws<IkeFormatException>(() => IkeResponderAuthentication.ReadSignatureHashSupport([new(IkePayloadType.Notify, body)]));
    }
}
