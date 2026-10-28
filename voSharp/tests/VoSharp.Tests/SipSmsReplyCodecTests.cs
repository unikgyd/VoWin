using VoSharp.Kernel.SipGateway;
using VoSharp.Telephony.Sms;

namespace VoSharp.Tests;

public sealed class SipSmsReplyCodecTests
{
    private static string Binding => SmsSubscriptionIdentity.ForIccid("8900000000000000001")!;

    [Theory]
    [InlineData("+15551234567")]
    [InlineData("15551234567")]
    [InlineData("1234567")]
    [InlineData("+123456789012345")]
    public void ProvenFullNumberRoundTripsWithTheOriginalCard(string number)
    {
        var user = SipGateway.SmsReplyUser("line-a", number, Binding);
        Assert.StartsWith("sms2-", user);
        Assert.InRange(user.Length, 1, 78);
        Assert.True(SipGateway.TryParseSmsReplyUser(user, out var line, out var binding, out var parsed));
        Assert.Equal(12, line.Length); Assert.Equal(Binding, binding); Assert.Equal(number, parsed);
        Assert.Equal(user, SipGateway.SmsReplyUser("LINE-A", number, Binding));
        Assert.NotEqual(user, SipGateway.SmsReplyUser("line-b", number, Binding));
    }

    [Theory]
    [InlineData(null, "+15551234567")]
    [InlineData("", "+15551234567")]
    [InlineData("not-a-proven-card", "+15551234567")]
    [InlineData("valid", "1069")]
    [InlineData("valid", "BANK")]
    [InlineData("valid", "123456")]
    [InlineData("valid", "+1234567890123456")]
    [InlineData("valid", "+1 5551234567")]
    [InlineData("valid", "+15551234567\n")]
    [InlineData("valid", "+15551234567\r\n")]
    [InlineData("valid", "15551234567\r\nX: injected")]
    public void UnprovenOrNonTelephoneSourcesAreReadOnly(string? binding, string sender)
    {
        if (binding == "valid") binding = Binding;
        var user = SipGateway.SmsReplyUser("line-a", sender, binding);
        Assert.StartsWith("sms-ro-", user);
        Assert.False(SipGateway.TryParseSmsReplyUser(user, out _, out _, out _));
        Assert.DoesNotContain(sender, user);
    }

    [Fact]
    public void LegacyOrTamperedAliasesNeverParseAsBoundReplies()
    {
        var valid = SipGateway.SmsReplyUser("line-a", "+15551234567", Binding);
        foreach (var user in new[] { SipGateway.SmsReplyUser("line-a", "+15551234567"),
            valid.Remove(18, 43).Insert(18, "unknown"), valid + "junk", valid + ";line=other", valid + "\n", valid + "\r\n", "sms2-",
            "sms2-012345abcdef-" + Binding + "-+15551234567" })
            Assert.False(SipGateway.TryParseSmsReplyUser(user, out _, out _, out _));
    }

    [Fact]
    public void NonCanonicalBase64PaddingCannotCreateAnotherSpellingOfTheSameReply()
    {
        var valid = SipGateway.SmsReplyUser("line-a", "+15551234567", Binding);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var last = alphabet.IndexOf(valid[60]);
        Assert.Equal(0, last & 3);
        var alternate = valid[..60] + alphabet[last + 1] + valid[61..];
        Assert.False(SipGateway.TryParseSmsReplyUser(alternate, out _, out _, out _));
    }

    [Fact]
    public void CaseSensitivePayloadIsNotSilentlyFoldedIntoAnotherCard()
    {
        var valid = SipGateway.SmsReplyUser("line-a", "+15551234567", Binding);
        var encoded = valid.Substring(18, 43);
        var index = Array.FindIndex(encoded.ToCharArray(), char.IsLetter);
        Assert.InRange(index, 0, 41); // Leave padding bits untouched.
        var changed = char.IsUpper(encoded[index]) ? char.ToLowerInvariant(encoded[index]) : char.ToUpperInvariant(encoded[index]);
        var alternate = valid[..(18 + index)] + changed + valid[(19 + index)..];
        Assert.True(SipGateway.TryParseSmsReplyUser(alternate, out _, out var other, out _));
        Assert.NotEqual(Binding, other);
    }
}
