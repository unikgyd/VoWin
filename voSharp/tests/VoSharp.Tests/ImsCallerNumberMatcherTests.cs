using VoSharp.Telephony.Calls;

namespace VoSharp.Tests;

public sealed class ImsCallerNumberMatcherTests
{
    [Theory]
    [InlineData("17387799413", "+8617387799413", true)]
    [InlineData("+86 173 8779 9413", "tel:17387799413", true)]
    [InlineData("17387799413", "17387799414", false)]
    [InlineData("17387799413", "008617387799413", false)]
    [InlineData("17387799413", "sip:+8617387799413@ims.example", false)]
    [InlineData("17387799413", "anonymous", false)]
    public void MatchesOnlyTheExactMobileIdentity(string expected, string received, bool match) =>
        Assert.Equal(match, ImsCallerNumberMatcher.Matches(expected, received));
}
