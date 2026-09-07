namespace VoSharp.Ike;

/// <summary>Thrown when a peer proposal names algorithms this stack does not implement.</summary>
public sealed class IkeUnsupportedSuiteException : Exception
{
    public IkeUnsupportedSuiteException(string message) : base(message) { }
}

/// <summary>
/// The negotiated IKE SA algorithm suite: one transform per type, plus the key and
/// checksum lengths the rest of the stack derives from them.
/// </summary>
/// <remarks>
/// Integrity truncation is implied by the algorithm name and never appears on the wire:
/// HMAC-SHA1-96 means a 20-byte key and a 12-byte ICV, HMAC-SHA2-256-128 a 32-byte key and
/// a 16-byte ICV. Mixing that pair up is the classic cause of a rejected first SK payload,
/// so the lengths live here, next to the IDs that imply them.
/// </remarks>
public sealed record IkeSuite(
    ushort EncryptionId,
    int EncryptionBits,
    ushort PrfId,
    ushort IntegrityId,
    ushort DhGroupId)
{
    /// <summary>AES-CBC block size, which is also the SK payload IV length (RFC 7296 3.14).</summary>
    public const int BlockSize = 16;

    /// <summary>
    /// What we offer first: AES-CBC-256 / PRF_HMAC_SHA2_256 / AUTH_HMAC_SHA2_256_128 / MODP-2048.
    /// MODP-2048 has to be the first proposal because the KE payload is built from it.
    /// </summary>
    public static IkeSuite Preferred { get; } = new(
        IkeEncryptionId.AesCbc, 256,
        IkePrfId.HmacSha2_256,
        IkeIntegrityId.HmacSha2_256_128,
        IkeDhGroupId.Modp2048);

    /// <summary>Length of SK_ei / SK_er.</summary>
    public int EncryptionKeyLength => EncryptionBits switch
    {
        128 => 16,
        192 => 24,
        256 => 32,
        _ => throw new IkeUnsupportedSuiteException(
            $"AES-CBC key length {EncryptionBits} bits is not supported (expected 128, 192 or 256).")
    };

    /// <summary>PRF output length, which is also the length of SK_d, SK_pi and SK_pr.</summary>
    public int PrfLength => PrfId switch
    {
        IkePrfId.HmacSha1 => 20,
        IkePrfId.HmacSha2_256 => 32,
        IkePrfId.HmacSha2_384 => 48,
        IkePrfId.HmacSha2_512 => 64,
        _ => throw new IkeUnsupportedSuiteException($"PRF {PrfId} is not supported.")
    };

    /// <summary>Full integrity key length (SK_ai / SK_ar), before ICV truncation.</summary>
    public int IntegrityKeyLength => IntegrityId switch
    {
        IkeIntegrityId.HmacSha1_96 => 20,
        IkeIntegrityId.HmacSha2_256_128 => 32,
        IkeIntegrityId.HmacSha2_384_192 => 48,
        IkeIntegrityId.HmacSha2_512_256 => 64,
        _ => throw new IkeUnsupportedSuiteException($"Integrity algorithm {IntegrityId} is not supported.")
    };

    /// <summary>Truncated ICV length appended to every SK payload.</summary>
    public int ChecksumLength => IntegrityId switch
    {
        IkeIntegrityId.HmacSha1_96 => 12,
        IkeIntegrityId.HmacSha2_256_128 => 16,
        IkeIntegrityId.HmacSha2_384_192 => 24,
        IkeIntegrityId.HmacSha2_512_256 => 32,
        _ => throw new IkeUnsupportedSuiteException($"Integrity algorithm {IntegrityId} is not supported.")
    };

    /// <summary>Encodes this suite as the transform list of an SA proposal.</summary>
    public List<IkeTransform> ToTransforms() => new()
    {
        new IkeTransform(IkeTransformType.Encryption, EncryptionId, (ushort)EncryptionBits),
        new IkeTransform(IkeTransformType.Prf, PrfId),
        new IkeTransform(IkeTransformType.Integrity, IntegrityId),
        new IkeTransform(IkeTransformType.DiffieHellman, DhGroupId)
    };

    /// <summary>
    /// Reads back the algorithms the responder chose. A chosen proposal carries exactly one
    /// transform per type (RFC 7296 3.3), so a repeat or a gap is a protocol error rather
    /// than a negotiation we could salvage.
    /// </summary>
    public static IkeSuite FromProposal(IkeProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        if (proposal.Protocol != IkeProtocolId.Ike)
            throw new IkeUnsupportedSuiteException(
                $"Expected an IKE proposal, got protocol {proposal.Protocol}.");

        var chosen = SelectOnePerType(proposal);

        var encryption = Require(chosen, IkeTransformType.Encryption, "encryption");
        var prf = Require(chosen, IkeTransformType.Prf, "PRF");
        var integrity = Require(chosen, IkeTransformType.Integrity, "integrity");
        var dh = Require(chosen, IkeTransformType.DiffieHellman, "Diffie-Hellman");

        if (encryption.Id != IkeEncryptionId.AesCbc)
            throw new IkeUnsupportedSuiteException(
                $"Only AES-CBC (ID {IkeEncryptionId.AesCbc}) is implemented; peer chose encryption {encryption.Id}.");

        if (encryption.KeyLengthBits is not { } bits)
            throw new IkeUnsupportedSuiteException(
                "AES-CBC transform is missing its key-length attribute, so the key size is ambiguous.");

        if (dh.Id is not (IkeDhGroupId.Modp1024 or IkeDhGroupId.Modp2048 or IkeDhGroupId.Ecp256))
            throw new IkeUnsupportedSuiteException(
                $"DH group {dh.Id} is not implemented. Supported groups are 2 (MODP-1024), 14 (MODP-2048), and 19 (ECP-256).");

        var suite = new IkeSuite(encryption.Id, bits, prf.Id, integrity.Id, dh.Id);
        suite.Validate();
        return suite;
    }

    /// <summary>
    /// Touches every length accessor so an unsupported ID fails here, while the proposal is
    /// still in hand, instead of halfway through key derivation.
    /// </summary>
    internal void Validate()
    {
        _ = EncryptionKeyLength;
        _ = PrfLength;
        _ = IntegrityKeyLength;
        _ = ChecksumLength;
    }

    internal static Dictionary<byte, IkeTransform> SelectOnePerType(IkeProposal proposal)
    {
        var chosen = new Dictionary<byte, IkeTransform>();

        foreach (var transform in proposal.Transforms)
        {
            if (!chosen.TryAdd(transform.Type, transform))
                throw new IkeUnsupportedSuiteException(
                    $"Proposal repeats transform type {transform.Type}; a chosen proposal carries one of each.");
        }

        return chosen;
    }

    internal static IkeTransform Require(
        IReadOnlyDictionary<byte, IkeTransform> transforms, byte type, string label) =>
        transforms.TryGetValue(type, out var transform)
            ? transform
            : throw new IkeUnsupportedSuiteException($"Proposal is missing its {label} transform.");
}

/// <summary>
/// The negotiated CHILD SA (ESP) algorithm suite. Smaller than <see cref="IkeSuite"/> on
/// purpose: ESP carries no PRF, and the DH group is inherited from the IKE SA.
/// </summary>
/// <remarks>
/// These values are handed to the Windows kernel through <c>VoSharp.Ipsec</c>. C# derives the
/// ESP keys but never encrypts or decrypts an ESP packet itself.
/// </remarks>
public sealed record EspSuite(ushort EncryptionId, int EncryptionBits, ushort IntegrityId)
{
    /// <summary>AES-CBC-128 + HMAC-SHA1-96, the pair carrier ePDGs accept universally.</summary>
    public static EspSuite Preferred { get; } = new(
        IkeEncryptionId.AesCbc, 128, IkeIntegrityId.HmacSha1_96);

    public int EncryptionKeyLength => EncryptionBits switch
    {
        128 => 16,
        192 => 24,
        256 => 32,
        _ => throw new IkeUnsupportedSuiteException(
            $"ESP AES-CBC key length {EncryptionBits} bits is not supported (expected 128, 192 or 256).")
    };

    public int IntegrityKeyLength => IntegrityId switch
    {
        IkeIntegrityId.HmacMd5_96 => 16,
        IkeIntegrityId.HmacSha1_96 => 20,
        IkeIntegrityId.HmacSha2_256_128 => 32,
        _ => throw new IkeUnsupportedSuiteException($"ESP integrity algorithm {IntegrityId} is not supported.")
    };

    /// <summary>Bytes of key material a CHILD SA needs per direction.</summary>
    public int KeyMaterialPerDirection => EncryptionKeyLength + IntegrityKeyLength;

    /// <summary>
    /// Encodes this suite as an ESP proposal transform list. Unlike IKE, ESP must state its
    /// sequence-number width explicitly (RFC 7296 3.3.2), so ESN=0 is always present.
    /// </summary>
    public List<IkeTransform> ToTransforms() => new()
    {
        new IkeTransform(IkeTransformType.Encryption, EncryptionId, (ushort)EncryptionBits),
        new IkeTransform(IkeTransformType.Integrity, IntegrityId),
        new IkeTransform(IkeTransformType.ExtendedSequenceNumbers, 0)
    };

    /// <summary>Reads back the ESP algorithms the responder chose in IKE_AUTH.</summary>
    public static EspSuite FromProposal(IkeProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        if (proposal.Protocol != IkeProtocolId.Esp)
            throw new IkeUnsupportedSuiteException(
                $"Expected an ESP proposal, got protocol {proposal.Protocol}.");

        if (proposal.Spi.Length != 4)
            throw new IkeUnsupportedSuiteException(
                $"An ESP proposal carries a 4-byte SPI; this one carries {proposal.Spi.Length}.");

        var chosen = IkeSuite.SelectOnePerType(proposal);

        var encryption = IkeSuite.Require(chosen, IkeTransformType.Encryption, "encryption");
        var integrity = IkeSuite.Require(chosen, IkeTransformType.Integrity, "integrity");

        if (encryption.Id != IkeEncryptionId.AesCbc)
            throw new IkeUnsupportedSuiteException(
                $"Only ESP AES-CBC (ID {IkeEncryptionId.AesCbc}) is implemented; peer chose {encryption.Id}.");

        if (encryption.KeyLengthBits is not { } bits)
            throw new IkeUnsupportedSuiteException(
                "ESP AES-CBC transform is missing its key-length attribute.");

        // 64-bit sequence numbers would need the kernel SA to agree; we never offer them.
        if (chosen.TryGetValue(IkeTransformType.ExtendedSequenceNumbers, out var esn) && esn.Id != 0)
            throw new IkeUnsupportedSuiteException(
                "Extended (64-bit) sequence numbers are not supported; expected ESN transform ID 0.");

        var suite = new EspSuite(encryption.Id, bits, integrity.Id);
        _ = suite.KeyMaterialPerDirection;
        return suite;
    }
}
