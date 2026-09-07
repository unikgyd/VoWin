using VoSharp.Ike;

namespace VoSharp.Ike.Tests;

public sealed class Ecp256GroupTests
{
    [Fact]
    public void Ecp256_UsesIkeWirePointEncoding_AndDerivesTheSameRawSecret()
    {
        using var initiator = Ecp256Group.Create();
        using var responder = Ecp256Group.Create();

        Assert.Equal(IkeDhGroupId.Ecp256, initiator.GroupId);
        Assert.Equal(64, initiator.Public.Length); // X(32) || Y(32), RFC 5903
        Assert.Equal(64, responder.Public.Length);

        var initiatorSecret = initiator.ComputeSharedSecret(responder.Public);
        var responderSecret = responder.ComputeSharedSecret(initiator.Public);

        Assert.Equal(32, initiatorSecret.Length);
        Assert.Equal(initiatorSecret, responderSecret);
    }

    [Fact]
    public void IkeSuite_AcceptsAResponderSelectedEcp256Proposal()
    {
        var proposal = new IkeProposal
        {
            ProposalNumber = 1,
            Protocol = IkeProtocolId.Ike,
            Transforms = new()
            {
                new IkeTransform(IkeTransformType.Encryption, IkeEncryptionId.AesCbc, 256),
                new IkeTransform(IkeTransformType.Prf, IkePrfId.HmacSha2_256),
                new IkeTransform(IkeTransformType.Integrity, IkeIntegrityId.HmacSha2_256_128),
                new IkeTransform(IkeTransformType.DiffieHellman, IkeDhGroupId.Ecp256)
            }
        };

        var suite = IkeSuite.FromProposal(proposal);

        Assert.Equal(IkeDhGroupId.Ecp256, suite.DhGroupId);
        Assert.Equal(256, suite.EncryptionBits);
    }
}
