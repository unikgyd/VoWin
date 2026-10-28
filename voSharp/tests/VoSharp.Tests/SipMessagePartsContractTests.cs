using System.Text;
using System.Text.Json;
using VoSharp.Kernel.SipGateway;

namespace VoSharp.Tests;

/// <summary>
/// Execute the existing pure splitter against independent shared expectations.
/// Actual SIP prefixes, transport guards, deadlines and per-contact completion
/// remain covered by the wire/worker suites; this class does not simulate them.
/// </summary>
public sealed class SipMessagePartsContractTests
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static JsonDocument Contract() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "contracts", "sip-message-parts-v1.json")));

    public static IEnumerable<object[]> Vectors()
    {
        using var document = Contract();
        foreach (var vector in document.RootElement.GetProperty("cases").EnumerateArray())
            yield return [vector.GetProperty("id").GetString()!, vector.GetRawText()];
    }

    private static string Expand(JsonElement fragments)
    {
        var text = new StringBuilder();
        foreach (var fragment in fragments.EnumerateArray())
        {
            var value = fragment.GetProperty("text").GetString()!;
            var repeat = fragment.GetProperty("repeat").GetInt32();
            Assert.InRange(repeat, 0, 16384);
            for (var index = 0; index < repeat; index++) text.Append(value);
        }
        return text.ToString();
    }

    [Fact]
    public void SharedContractDistinguishesRawChunksFromAdmissionAndWirePolicy()
    {
        using var document = Contract();
        var contract = document.RootElement;
        Assert.Equal(1, contract.GetProperty("version").GetInt32());
        Assert.Equal(280, contract.GetProperty("rawChunkUtf8Bytes").GetInt32());
        Assert.Equal(1300, contract.GetProperty("maxSipDatagramBytes").GetInt32());
        Assert.Equal("[{index}/{count}] ", contract.GetProperty("multipartPrefix").GetString());
        Assert.True(contract.GetProperty("singlePartUnchanged").GetBoolean());
        var admission = contract.GetProperty("inputAdmission");
        Assert.Equal(16384, admission.GetProperty("maxUtf16Units").GetInt32());
        Assert.True(admission.GetProperty("strictUnicode").GetBoolean());
        Assert.True(admission.GetProperty("rejectEmpty").GetBoolean());
        Assert.True(admission.GetProperty("rejectNul").GetBoolean());
        var cases = contract.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(12, cases.Length);
        Assert.Equal(cases.Length, cases.Select(value => value.GetProperty("id").GetString()).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void SharedScalarBoundariesMatchIndependentExpectedChunks(string id, string json)
    {
        Assert.False(string.IsNullOrWhiteSpace(id));
        using var document = JsonDocument.Parse(json);
        var vector = document.RootElement;
        var text = Expand(vector.GetProperty("input"));
        var expected = vector.GetProperty("expectedChunks").EnumerateArray().Select(Expand).ToArray();
        var actual = SipGateway.SplitMessageText(text).ToArray();
        Assert.Equal(expected, actual);
        Assert.Equal(text, string.Concat(actual));
        Assert.Equal(text, StrictUtf8.GetString(StrictUtf8.GetBytes(text)));
        Assert.InRange(text.Length, 1, 16384);
        Assert.DoesNotContain('\0', text);
        foreach (var chunk in actual)
        {
            Assert.InRange(StrictUtf8.GetByteCount(chunk), 1, 280);
            Assert.Equal(chunk, StrictUtf8.GetString(StrictUtf8.GetBytes(chunk)));
        }
        // Prefix expectations are fixed data for actual transport tests. They
        // are deliberately not mistaken for output of this raw splitter.
        Assert.Equal(expected.Length, vector.GetProperty("wirePrefixes").GetArrayLength());
    }

    [Theory]
    [InlineData("A", 16384, 59)]
    [InlineData("中", 16384, 177)]
    [InlineData("🌍", 8192, 118)]
    [InlineData("A", 16385, 0)]
    [InlineData("🌍", 8193, 0)]
    public void SplitterLimitCountsUtf16UnitsWithoutLosingValidScalars(string scalar, int repeat, int expectedParts)
    {
        var text = string.Concat(Enumerable.Repeat(scalar, repeat));
        if (expectedParts == 0)
        {
            Assert.True(text.Length > 16384);
            Assert.Throws<ArgumentException>(() => SipGateway.SplitMessageText(text));
            return;
        }
        Assert.Equal(16384, text.Length);
        var actual = SipGateway.SplitMessageText(text);
        Assert.Equal(expectedParts, actual.Count);
        Assert.Equal(text, string.Concat(actual));
        foreach (var chunk in actual) Assert.InRange(StrictUtf8.GetByteCount(chunk), 1, 280);
    }
}
