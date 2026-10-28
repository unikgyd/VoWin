using System.Formats.Asn1;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace VoSharp.Ike;

/// <summary>Per-attempt trust. Explicit roots replace, never modify, the system trust store.</summary>
public sealed class IkeResponderAuthenticationOptions
{
    public string ServerName { get; }
    internal byte[][]? Roots { get; }

    public IkeResponderAuthenticationOptions(string serverName, IEnumerable<byte[]>? trustedRootCertificates = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ServerName = serverName.Trim().TrimEnd('.');
        Roots = trustedRootCertificates?.Select(c => c.ToArray()).ToArray();
        if (Roots is { Length: > 32 } || Roots?.Any(c => c.Length is 0 or > 65536) == true || Roots?.Sum(c => (long)c.Length) > 262144)
            throw new ArgumentException("Invalid responder trust-root bounds.", nameof(trustedRootCertificates));
    }
}

internal static class IkeResponderAuthentication
{
    internal const ushort SignatureHashAlgorithms = 16431;
    private const string IkePurpose = "1.3.6.1.5.5.7.3.17";

    internal static bool ReadSignatureHashSupport(IReadOnlyList<IkePayload> payloads)
    {
        var notes = payloads.Where(p => p.Type == IkePayloadType.Notify && p.Body.Length >= 4 &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(p.Body.AsSpan(2)) == SignatureHashAlgorithms).ToArray();
        if (notes.Length > 1 || notes.Any(n => n.Body[1] != 0 || n.Body.Length < 6 || n.Body.Length % 2 != 0))
            throw new IkeFormatException("Invalid SIGNATURE_HASH_ALGORITHMS notification.");
        return notes.Length == 1; // Unknown hash IDs do not alter our SHA-2 receive policy.
    }

    internal static bool Verify(IReadOnlyList<IkePayload> payloads, byte[] initialResponse, byte[] nonce,
        IkeSuite suite, byte[] skPr, IkeResponderAuthenticationOptions options, bool genericNegotiated)
    {
        var auths = payloads.Where(p => p.Type == IkePayloadType.Authentication).ToArray();
        if (auths.Length == 0) return false; // EAP-only: never turn absence into early authentication.
        var ids = payloads.Where(p => p.Type == IkePayloadType.IdentificationResponder).ToArray();
        if (auths.Length != 1 || ids.Length != 1 || ids[0].Body.Length < 5 || ids[0].Body[0] != 2 ||
            ids[0].Body.AsSpan(4).Contains((byte)0) || auths[0].Body.Length < 5)
            throw new AuthenticationException("Initial responder AUTH requires one valid ID_FQDN and AUTH.");
        if (auths[0].Body[0] == 14 && !genericNegotiated)
            throw new AuthenticationException("Generic responder signature was not negotiated.");

        var owned = new List<X509Certificate2>();
        byte[]? idHash = null, signed = null;
        try
        {
            var certificates = payloads.Where(p => p.Type == IkePayloadType.Certificate).ToArray();
            if (certificates.Length is 0 or > 16 || certificates[0].Body.Length < 2 || certificates[0].Body[0] != 4)
                throw new AuthenticationException("Initial responder AUTH has no usable signing certificate.");
            foreach (var certificate in certificates)
            {
                if (certificate.Body.Length < 1) throw new AuthenticationException("Truncated responder certificate.");
                if (certificate.Body[0] != 4) continue; // Unrelated later encodings are not trust anchors or URLs to fetch.
                owned.Add(X509CertificateLoader.LoadCertificate(certificate.Body.AsSpan(1)));
            }
            var leaf = owned[0];
            if (!leaf.MatchesHostname(options.ServerName, allowWildcards: true, allowCommonName: false))
                throw new AuthenticationException("Responder certificate does not match the selected ePDG name.");
            if (leaf.Extensions.OfType<X509BasicConstraintsExtension>().Any(e => e.CertificateAuthority) ||
                leaf.Extensions.OfType<X509KeyUsageExtension>().Any(e => (e.KeyUsages & X509KeyUsageFlags.DigitalSignature) == 0))
                throw new AuthenticationException("Responder certificate is not an end-entity signing certificate.");
            using var chain = new X509Chain();
            chain.ChainPolicy.DisableCertificateDownloads = true;
            // No network lookups or claim of revocation/OCSP verification in this verifier.
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid(IkePurpose));
            foreach (var intermediate in owned.Skip(1)) chain.ChainPolicy.ExtraStore.Add(intermediate);
            if (options.Roots != null)
            {
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                foreach (var raw in options.Roots)
                {
                    var root = X509CertificateLoader.LoadCertificate(raw);
                    owned.Add(root); chain.ChainPolicy.CustomTrustStore.Add(root);
                }
            }
            if (!chain.Build(leaf)) throw new AuthenticationException("Responder certificate chain, validity or IKE purpose is invalid.");
            idHash = IkeCrypto.Prf(suite, skPr, ids[0].Body);
            signed = [.. initialResponse, .. nonce, .. idHash];
            VerifySignature(leaf, auths[0].Body[0], auths[0].Body.AsSpan(4), signed);
            return true;
        }
        catch (Exception error) when (error is CryptographicException or AsnContentException or ArgumentException or OverflowException)
        { throw new AuthenticationException("Invalid initial responder certificate or signature encoding.", error); }
        finally
        {
            foreach (var certificate in owned) certificate.Dispose();
            if (idHash != null) CryptographicOperations.ZeroMemory(idHash);
            if (signed != null) CryptographicOperations.ZeroMemory(signed);
        }
    }

    private static void VerifySignature(X509Certificate2 certificate, byte method, ReadOnlySpan<byte> signature, byte[] signed)
    {
        HashAlgorithmName hash;
        bool rsa, pss = false;
        var derEcdsa = false;
        if (method == 1) { hash = HashAlgorithmName.SHA1; rsa = true; }
        else if (method is 9 or 10 or 11)
        {
            hash = method == 9 ? HashAlgorithmName.SHA256 : method == 10 ? HashAlgorithmName.SHA384 : HashAlgorithmName.SHA512;
            rsa = false;
        }
        else if (method == 14)
        {
            if (signature.Length < 3 || signature[0] == 0 || signature[0] >= signature.Length - 1)
                throw new AuthenticationException("Invalid generic signature AlgorithmIdentifier length.");
            var length = signature[0];
            var reader = new AsnReader(signature.Slice(1, length).ToArray(), AsnEncodingRules.DER);
            var algorithm = reader.ReadSequence(); reader.ThrowIfNotEmpty();
            var oid = algorithm.ReadObjectIdentifier();
            (hash, rsa) = oid switch
            {
                "1.2.840.113549.1.1.11" => (HashAlgorithmName.SHA256, true),
                "1.2.840.113549.1.1.12" => (HashAlgorithmName.SHA384, true),
                "1.2.840.113549.1.1.13" => (HashAlgorithmName.SHA512, true),
                "1.2.840.10045.4.3.2" => (HashAlgorithmName.SHA256, false),
                "1.2.840.10045.4.3.3" => (HashAlgorithmName.SHA384, false),
                "1.2.840.10045.4.3.4" => (HashAlgorithmName.SHA512, false),
                "1.2.840.113549.1.1.10" => (ReadPss(algorithm), true),
                _ => throw new AuthenticationException("Unsupported responder signature algorithm.")
            };
            pss = oid == "1.2.840.113549.1.1.10";
            if (!pss && algorithm.HasData) algorithm.ReadNull();
            algorithm.ThrowIfNotEmpty();
            signature = signature[(length + 1)..];
            derEcdsa = true;
        }
        else throw new AuthenticationException("Unsupported initial responder AUTH method.");

        bool valid;
        if (rsa)
        {
            using var key = certificate.GetRSAPublicKey();
            valid = key != null && key.VerifyData(signed, signature, hash, pss ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1);
        }
        else
        {
            using var key = certificate.GetECDsaPublicKey();
            var requiredSize = method == 9 ? 256 : method == 10 ? 384 : method == 11 ? 521 : 0;
            valid = key != null && (requiredSize == 0 || key.KeySize == requiredSize) && key.VerifyData(signed, signature, hash,
                derEcdsa ? DSASignatureFormat.Rfc3279DerSequence : DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        if (!valid) throw new AuthenticationException("Initial responder AUTH signature is invalid.");
    }

    private static HashAlgorithmName ReadPss(AsnReader algorithm)
    {
        var parameters = algorithm.ReadSequence();
        var hash = HashAlgorithmName.SHA1; var mgf = HashAlgorithmName.SHA1; var salt = 20; var trailer = 1; var last = -1;
        while (parameters.HasData)
        {
            var tag = parameters.PeekTag();
            if (tag.TagClass != TagClass.ContextSpecific || tag.TagValue <= last || tag.TagValue > 3) throw new AsnContentException();
            last = tag.TagValue; var value = parameters.ReadSequence(tag);
            switch (tag.TagValue)
            {
                case 0: hash = ReadHash(value.ReadSequence()); break;
                case 1:
                    var mask = value.ReadSequence();
                    if (mask.ReadObjectIdentifier() != "1.2.840.113549.1.1.8") throw new AsnContentException();
                    mgf = ReadHash(mask.ReadSequence()); mask.ThrowIfNotEmpty(); break;
                case 2: salt = checked((int)value.ReadInteger()); break;
                case 3: trailer = checked((int)value.ReadInteger()); break;
            }
            value.ThrowIfNotEmpty();
        }
        var size = hash == HashAlgorithmName.SHA256 ? 32 : hash == HashAlgorithmName.SHA384 ? 48 : hash == HashAlgorithmName.SHA512 ? 64 : 0;
        if (size == 0 || mgf != hash || salt != size || trailer != 1) throw new AuthenticationException("Unsupported RSA-PSS parameters.");
        return hash;
    }

    private static HashAlgorithmName ReadHash(AsnReader sequence)
    {
        var hash = sequence.ReadObjectIdentifier() switch
        {
            "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
            "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
            "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
            "1.3.14.3.2.26" => HashAlgorithmName.SHA1,
            _ => throw new AuthenticationException("Unsupported RSA-PSS hash.")
        };
        if (sequence.HasData) sequence.ReadNull(); sequence.ThrowIfNotEmpty(); return hash;
    }
}
