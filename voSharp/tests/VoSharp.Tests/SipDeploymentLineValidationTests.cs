using System.Text.Json;
using VoSharp.Kernel.SipGateway;

namespace VoSharp.Tests;

public sealed partial class SipServerFeatureTests
{
    [Theory]
    [InlineData("123456789")]
    [InlineData("+8")]
    [InlineData("８")]
    [InlineData(" 8")]
    [InlineData("8 ")]
    [InlineData("8\n")]
    [InlineData("8\r\n")]
    [InlineData("*")]
    public void DeploymentProjectionLineValidationRejectsUnsafeOrUnboundedSelectionPrefixes(string prefix)
    {
        var options = DeploymentProjectionLineOptions();
        options.LineRoutes["modem-a"] = options.LineRoutes["modem-a"] with { Prefix = prefix };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Theory]
    [InlineData(null, 6)]
    [InlineData("", 6)]
    [InlineData("0", 6)]
    [InlineData("012", 6)]
    [InlineData("1234", 6)]
    [InlineData("86", 5)]
    [InlineData("86", 14)]
    [InlineData("1", 15)]
    [InlineData("63\n", 10)]
    [InlineData("６３", 10)]
    [InlineData("63", -1)]
    public void DeploymentProjectionLineValidationRejectsIncompleteOrInvalidNationalPolicies(string? country, int length)
    {
        var options = DeploymentProjectionLineOptions();
        options.LineRoutes["modem-a"] = options.LineRoutes["modem-a"] with
        { CountryCode = country, NationalNumberLength = length };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("1", 6)]
    [InlineData("1", 14)]
    [InlineData("86", 13)]
    [InlineData("123", 12)]
    public void DeploymentProjectionLineValidationAcceptsExactNationalBoundsWithoutInferringACountry(string? country, int length)
    {
        var options = DeploymentProjectionLineOptions();
        options.LineRoutes["modem-a"] = options.LineRoutes["modem-a"] with
        { Prefix = null, CountryCode = country, NationalNumberLength = length };
        options.Validate();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeploymentProjectionLineValidationRejectsDuplicatePrefixesEvenOnADisabledLine(bool disabled)
    {
        var options = DeploymentProjectionLineOptions();
        options.LineRoutes["modem-b"] = options.LineRoutes["modem-b"] with { Prefix = "8", Enabled = !disabled };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeploymentProjectionLineValidationStrictModeRequiresAnExactE164NumberWhenNoCountryWasConfigured(bool sms)
    {
        var options = DeploymentProjectionLineOptions();
        options.LineRoutes["modem-a"] = options.LineRoutes["modem-a"] with
        { Prefix = null, CountryCode = null, NationalNumberLength = 0 };
        options.Validate();
        foreach (var number in new[] { "+01234567", "+123456", "+1234567890123456", "+15551234567\n", "1234567", "*123#", "＋15551234567" })
            Assert.Equal(484, Assert.Throws<SipGatewayLineRoutingException>(() =>
                options.ResolveDestination(number, "modem-a", "alice", sms)).StatusCode);
        Assert.Equal("+1234567", options.ResolveDestination("+1234567", "modem-a", "alice", sms).Number);
        Assert.Equal("+123456789012345", options.ResolveDestination("+123456789012345", "modem-a", "alice", sms).Number);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeploymentProjectionLineValidationNationalConversionUsesOnlyAsciiDigitsAndExactLength(bool sms)
    {
        var options = DeploymentProjectionLineOptions();
        foreach (var number in new[] { "١٣٨٠٠١٣٨٠٠٠", "1380013800", "138001380000", "13800138000\n" })
            Assert.Equal(484, Assert.Throws<SipGatewayLineRoutingException>(() =>
                options.ResolveDestination(number, "modem-a", "alice", sms)).StatusCode);
        Assert.Equal("+8613800138000", options.ResolveDestination("13800138000", "modem-a", "alice", sms).Number);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeploymentProjectionLineValidationLegacyModeRetainsItsPermissiveNumbersWithoutNationalConversion(bool sms)
    {
        var options = DeploymentProjectionLineOptions();
        options.LineRoutes["modem-a"] = options.LineRoutes["modem-a"] with { NormalizeE164 = false };
        Assert.Equal("13800138000", options.ResolveDestination("13800138000", "modem-a", "alice", sms).Number);
        Assert.Equal("*123#", options.ResolveDestination("*123#", "modem-a", "alice", sms).Number);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeploymentProjectionLineValidationDormantDefaultKeepsItsIdentityButGrantsNoPaidDirection(bool sms)
    {
        var options = DeploymentProjectionLineOptions();
        options.LineRoutes["modem-a"] = options.LineRoutes["modem-a"] with
        { AllowOutgoingCalls = false, AllowOutgoingSms = false };
        options.Validate();
        Assert.Equal("modem-a", options.DefaultOutboundLineId);
        Assert.Equal(403, Assert.Throws<SipGatewayLineRoutingException>(() =>
            options.ResolveDestination("13800138000", null, "alice", sms)).StatusCode);
        Assert.Throws<ArgumentException>(() => (options with { DefaultOutboundLineId = "missing-line" }).Validate());
        options.LineRoutes["modem-a"] = options.LineRoutes["modem-a"] with { Enabled = false };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeploymentProjectionLineValidationPermissionDenialPrecedesNumberNormalization(bool sms)
    {
        var options = DeploymentProjectionLineOptions();
        options.ExtensionPolicies["alice"] = new()
        { AllowCalls = true, AllowSms = true, OutgoingLines = ["modem-a"], SmsSendLines = ["modem-a"] };
        Assert.Equal(403, Assert.Throws<SipGatewayLineRoutingException>(() =>
            options.ResolveDestination("not-a-phone", "modem-b", "alice", sms)).StatusCode);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DeploymentProjectionLineValidationLongestPrefixDenialNeverFallsBackToTheShorterPrefix(bool sms, bool disabled)
    {
        var options = DeploymentProjectionLineOptions();
        options.LineRoutes["modem-b"] = options.LineRoutes["modem-b"] with
        { Enabled = !disabled, AllowOutgoingCalls = false, AllowOutgoingSms = false };
        options.Validate();
        Assert.Equal(403, Assert.Throws<SipGatewayLineRoutingException>(() =>
            options.ResolveDestination("819171234567", null, "alice", sms)).StatusCode);
    }

    [Fact]
    public void DeploymentProjectionLineValidationNewMetadataDoesNotAlterTheLegacyDefaultJson()
    {
        Assert.Equal("{\"Enabled\":true,\"AllowIncomingCalls\":true,\"AllowOutgoingCalls\":true,\"IncomingTarget\":null}",
            JsonSerializer.Serialize(new SipGatewayLineRoute()));
        var source = DeploymentProjectionRoute("81", "63", 10);
        var copy = source.DeepClone();
        Assert.Equal(source.Prefix, copy.Prefix); Assert.Equal(source.CountryCode, copy.CountryCode);
        Assert.Equal(source.NationalNumberLength, copy.NationalNumberLength); Assert.True(copy.NormalizeE164);
        Assert.Equal(JsonSerializer.Serialize(source), JsonSerializer.Serialize(copy));
    }
}
