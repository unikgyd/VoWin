using System.Buffers.Binary;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;

namespace VoSharp.Ike;

internal static class EapAkaValidation
{
    internal const int MaximumPacket = 8192;
    internal static void Attributes(IReadOnlyList<AkaAttribute> attributes, params byte[] allowed)
    {
        foreach (var a in attributes)
        {
            if (!allowed.Contains(a.Type) && (a.Type < 128 || a.Type is 129 or 130 or 132 or 133 or 134 or 135 or 136))
                throw new AuthenticationException("Unexpected or unsupported mandatory EAP-AKA attribute.");
            var length = a.Type switch
            {
                1 or 2 or 11 or 129 => 20,
                10 or 12 or 13 or 17 or 22 or 24 or 135 or 136 => 4,
                _ => 0
            };
            if (length != 0 && a.Raw.Length != length)
                throw new AuthenticationException("Invalid EAP-AKA attribute length.");
        }
    }

    internal static byte[] NetworkName(AkaAttribute? attribute, string? expected)
    {
        if (attribute == null || attribute.Raw.Length < 4) throw new AuthenticationException("Missing AT_KDF_INPUT.");
        var size = BinaryPrimitives.ReadUInt16BigEndian(attribute.Raw.AsSpan(2, 2));
        if (size == 0 || size > attribute.Raw.Length - 4 || attribute.Raw.Length - 4 - size >= 4 ||
            attribute.Raw.AsSpan(4 + size).ContainsAnyExcept((byte)0))
            throw new AuthenticationException("Invalid AKA' network name length or padding.");
        var bytes = attribute.Raw.AsSpan(4, size).ToArray();
        string name;
        try { name = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw new AuthenticationException("Invalid AKA' network name encoding."); }
        if (name.Any(char.IsControl) || expected != null && !NetworkMatches(expected, name))
            throw new AuthenticationException("AKA' network name does not match the configured policy.");
        return bytes;
    }
    internal static bool NetworkMatches(string expected, string actual)
        => expected.Split(':').Zip(actual.Split(':')).All(fields => fields.First == fields.Second);

    internal static void Checkcode(IReadOnlyList<AkaAttribute> attributes, byte[] transcript, bool required, bool prime)
    {
        var check = attributes.FirstOrDefault(a => a.Type == 134);
        if (required && check == null) throw new AuthenticationException("AKA identity extension requires AT_CHECKCODE.");
        if (check == null) return;
        var expected = transcript.Length == 0 ? [] : prime ? SHA256.HashData(transcript) : SHA1.HashData(transcript);
        if (check.Raw.Length != 4 + expected.Length || !CryptographicOperations.FixedTimeEquals(check.Raw.AsSpan(4), expected))
            throw new AuthenticationException("EAP-AKA identity checkcode mismatch.");
    }

    internal static void EncryptedAttributes(IReadOnlyList<AkaAttribute> attributes, AkaDerivedKeys keys)
    {
        var iv = attributes.FirstOrDefault(a => a.Type == 129);
        var encrypted = attributes.FirstOrDefault(a => a.Type == 130);
        if ((iv == null) != (encrypted == null)) throw new AuthenticationException("AT_IV and AT_ENCR_DATA must occur together.");
        if (iv == null) return;
        if (iv.Raw.Length != 20 || encrypted!.Raw.Length < 20 || (encrypted.Raw.Length - 4) % 16 != 0)
            throw new AuthenticationException("Invalid EAP-AKA encrypted attribute size.");
        using var aes = Aes.Create(); aes.Key = keys.KEncr;
        var plaintext = aes.DecryptCbc(encrypted.Raw.AsSpan(4), iv.Raw.AsSpan(4), PaddingMode.None);
        try
        {
            var inner = EapAkaClient.ParseAkaAttributes(plaintext);
            try
            {
                Attributes(inner, 6, 132, 133);
                foreach (var a in inner)
                {
                    if (a.Type == 6 && a.Raw.AsSpan(2).ContainsAnyExcept((byte)0))
                        throw new AuthenticationException("Invalid AT_PADDING.");
                    if (a.Type is 132 or 133)
                    {
                        var length = BinaryPrimitives.ReadUInt16BigEndian(a.Raw.AsSpan(2, 2));
                        if (length == 0 || length > a.Raw.Length - 4 || a.Raw.Length - 4 - length >= 4)
                            throw new AuthenticationException("Invalid encrypted AKA identity length.");
                        _ = new UTF8Encoding(false, true).GetCharCount(a.Raw.AsSpan(4, length));
                    }
                }
            }
            finally { foreach (var a in inner) CryptographicOperations.ZeroMemory(a.Raw); }
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        // No pseudonym/fast-reauth identity is retained or advertised yet.
    }
}
