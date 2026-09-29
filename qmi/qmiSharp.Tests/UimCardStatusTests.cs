using qmiSharp.Services;
using Xunit;

namespace qmiSharp.Tests;

public sealed class UimCardStatusTests
{
    [Theory]
    [InlineData(0, UimCardState.Absent)]
    [InlineData(1, UimCardState.Present)]
    [InlineData(2, UimCardState.Error)]
    public void ParsesSingleCardStateAfterApplicationIndexes(byte rawState, UimCardState expected)
    {
        var value = new byte[15];
        value[8] = 1;
        value[9] = rawState;
        var status = UimService.ParseCardStatus(value);
        Assert.Equal(expected, status.State);
        Assert.Equal(1, status.NumCards);
        Assert.False(status.HasSubscriptionApplication);
    }

    [Fact]
    public void PresentEuiccWithoutSelectedSubscriptionFallsBackInsteadOfClaimingSimReady()
    {
        var noApplication = new byte[15];
        noApplication[8] = 1;
        noApplication[9] = 1;
        Assert.False(UimService.ParseCardStatus(noApplication).HasSubscriptionApplication);

        var withUsim = new byte[29];
        withUsim[8] = 1;
        withUsim[9] = 1;
        withUsim[14] = 1;
        withUsim[15] = 2;
        withUsim[16] = 7;
        Assert.True(UimService.ParseCardStatus(withUsim).HasSubscriptionApplication);

        withUsim[0] = 0xFF;
        withUsim[1] = 0xFF;
        Assert.False(UimService.ParseCardStatus(withUsim).HasSubscriptionApplication);
    }

    [Fact]
    public void SelectsGwPrimaryFromSecondCardAndSecondApplication()
    {
        // GW primary index 0x0101 means card 1, application 1 (both zero based).
        var value = new byte[9 + 6 + 6 + 14 + 14];
        value[0] = 1;
        value[1] = 1;
        value[8] = 2;
        value[9] = 0; // First card absent.
        value[15] = 1; // Second card present.
        value[20] = 2; // Two applications on the second card.
        value[21] = 5; // ISIM is not the selected GW subscription.
        value[22] = 7;
        value[35] = 2; // Selected USIM.
        value[36] = 7;

        var status = UimService.ParseCardStatus(value);
        Assert.Equal(UimCardState.Present, status.State);
        Assert.Equal(2, status.NumCards);
        Assert.Equal((byte)1, status.SelectedCardIndex);
        Assert.Equal((byte)1, status.SelectedApplicationIndex);
        Assert.True(status.HasSubscriptionApplication);

        value[35] = 5;
        Assert.False(UimService.ParseCardStatus(value).HasSubscriptionApplication);
    }

    [Fact]
    public void RejectsTruncatedLaterApplicationWithoutClaimingAnAbsentCard()
    {
        var value = new byte[9 + 6 + 6 + 14 + 8];
        value[0] = 1;
        value[1] = 1;
        value[8] = 2;
        value[15] = 1;
        value[20] = 2;
        value[21] = 2;
        value[22] = 7;
        value[41] = 5; // Claims an AID larger than the remaining bytes.
        Assert.Throws<FormatException>(() => UimService.ParseCardStatus(value));
    }

    [Fact]
    public void PreservesSelectedCardsApplicationAidsForIsimDiscovery()
    {
        var value = new byte[9 + 6 + 14 + 7 + 7 + 7];
        value[8] = 1;
        value[9] = 1;
        value[14] = 2;
        value[15] = 2;
        value[16] = 7;
        value[21] = 0;
        value[29] = 5;
        value[30] = 7;
        value[35] = 7;
        Convert.FromHexString("A0000000871004").CopyTo(value.AsSpan(36, 7));
        var status = UimService.ParseCardStatus(value);

        Assert.Equal(2, status.SelectedCardApplications.Count);
        Assert.Equal((byte)5, status.SelectedCardApplications[1].Type);
        Assert.Equal("A0000000871004", Convert.ToHexString(status.SelectedCardApplications[1].Aid));
    }

    [Fact]
    public void MissingOrAmbiguousCardStateIsNotReportedAsNoSim()
    {
        Assert.Throws<FormatException>(() => UimService.ParseCardStatus(new byte[8]));
        Assert.Throws<FormatException>(() => UimService.ParseCardStatus(new byte[9]));
        var truncated = new byte[14];
        truncated[8] = 1;
        Assert.Throws<FormatException>(() => UimService.ParseCardStatus(truncated));
        var multiSlot = new byte[15];
        multiSlot[8] = 2;
        Assert.Throws<FormatException>(() => UimService.ParseCardStatus(multiSlot));
    }

    [Fact]
    public void ApduResponseRequiresExactLengthAndStatusWords()
    {
        Assert.Equal([0x90, 0x00], UimService.ParseApduResponse([2, 0, 0x90, 0x00]));
        Assert.Throws<FormatException>(() => UimService.ParseApduResponse([2, 0, 0x90]));
        Assert.Throws<FormatException>(() => UimService.ParseApduResponse([2, 0, 0x90, 0, 0]));
        Assert.Throws<FormatException>(() => UimService.ParseApduResponse([1, 0, 0x90]));
        Assert.Throws<FormatException>(() => UimService.ParseApduResponse([0]));
    }
}
