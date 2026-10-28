using VoSharp.Ike;

namespace VoSharp.Ike.Tests;

public class IkeBackoffWireReviewTests
{
    // TS 24.302 8.2.9.1 refers to GPRS Timer 3, not GPRS Timer.
    [Theory]
    [InlineData("0101", "10 minutes")]
    [InlineData("0121", "1 hours")]
    [InlineData("0141", "10 hours")]
    [InlineData("0161", "2 seconds")]
    [InlineData("0181", "30 seconds")]
    [InlineData("01AA", "10 minutes")]
    [InlineData("01C1", "1 hours")]
    [InlineData("01E0", "does not expire")]
    [InlineData("0100", "0 seconds")]
    public void ExistingDiagnosticMustDecodeTw3Units(string data, string expected)
        => Assert.Equal(expected, IkeSession.DescribeBackoffTimer(Convert.FromHexString(data)));
}
