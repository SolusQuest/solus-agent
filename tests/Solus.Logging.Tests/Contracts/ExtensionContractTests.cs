using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Solus.Telemetry.Contracts;
using Xunit;

namespace Solus.Logging.Tests.Contracts;

public sealed class ExtensionContractTests
{
    [Fact]
    public void NestedValuesArePreservedAndCallerCompositionOnlyAddsItsOwnMembers()
    {
        const string extensions = "{\"scribe\":{\"ratio\":0.125,\"precise\":\"9007199254740993\",\"nested\":[true,null,{\"text\":\"你好\"}]},\"caller\":{\"team\":\"test\"}}";
        var source = SyntheticTelemetry.Produce(extensions: SyntheticTelemetry.Json(extensions));
        var merged = source.WithCallerAttributes(SyntheticTelemetry.Json("{\"costCenter\":\"platform\",\"usage\":{\"label\":1}}"));
        Assert.Equal(source.EventId, merged.EventId);
        Assert.Equal(JsonSerializer.Serialize(source.Extensions.GetProperty("scribe")), JsonSerializer.Serialize(merged.Extensions.GetProperty("scribe")));
        Assert.Equal("test", merged.Extensions.GetProperty("caller").GetProperty("team").GetString());
        Assert.Equal("platform", merged.Extensions.GetProperty("caller").GetProperty("costCenter").GetString());
        Assert.Equal(source.Usage, merged.Usage);
        Assert.False(source.Extensions.GetProperty("caller").TryGetProperty("costCenter", out _));
        var read = SyntheticTelemetry.Consume(SyntheticTelemetry.Wire(merged));
        Assert.Equal(0.125, read.Extensions.GetProperty("scribe").GetProperty("ratio").GetDouble());
        Assert.Equal("9007199254740993", read.Extensions.GetProperty("scribe").GetProperty("precise").GetString());
        Assert.Throws<ArgumentException>(() => source.WithCallerAttributes(SyntheticTelemetry.Json("{\"team\":\"other\"}")));
        Assert.Throws<ArgumentException>(() => source.WithCallerAttributes(SyntheticTelemetry.Json("[1]")));
        Assert.Throws<ArgumentException>(() => source.WithCallerAttributes(SyntheticTelemetry.Json("{\"x\":1,\"\\u0078\":2}")));
    }

    [Theory]
    [InlineData("{\"eventId\":{}}")]
    [InlineData("{\"apr\":{}}")]
    [InlineData("{\"bad namespace\":{}}")]
    [InlineData("{\"scribe\":1}")]
    [InlineData("{\"caller\":null}")]
    [InlineData("[]")]
    [InlineData("{\"scribe\":{\"a\":1,\"\\u0061\":2}}")]
    [InlineData("{\"scribe\":{\"array\":[{\"x\":1,\"x\":2}]}}")]
    [InlineData("{\"scribe\":{\"x\":9007199254740992}}")]
    [InlineData("{\"scribe\":{\"x\":9007199254740993}}")]
    [InlineData("{\"scribe\":{\"x\":9.007199254740992e15}}")]
    [InlineData("{\"scribe\":{\"x\":-9007199254740992}}")]
    [InlineData("{\"scribe\":{\"x\":1e400}}")]
    [InlineData("{\"scribe\":{\"x\":1e-400}}")]
    [InlineData("{\"scribe\":{\"text\":\"\\ud800\"}}")]
    public void InvalidOrUnsafeExtensionsRejectAtBothAdmissionBoundaries(string extensions)
    {
        Assert.ThrowsAny<ArgumentException>(() => SyntheticTelemetry.Produce(extensions: SyntheticTelemetry.Json(extensions)));
        var node = JsonNode.Parse(SyntheticTelemetry.Wire(SyntheticTelemetry.Produce()))!.AsObject();
        // Build text without converting duplicate JSON objects through a mutable dictionary.
        node.Remove("extensions");
        var prefix = node.ToJsonString();
        TelemetryContractTests.Rejected(prefix[..^1] + ",\"extensions\":" + extensions + "}");
    }

    [Theory]
    [InlineData("9007199254740991")]
    [InlineData("9.007199254740991e15")]
    [InlineData("-9007199254740991")]
    [InlineData("0.1234567890123456789012345678")]
    [InlineData("1e-300")]
    [InlineData("0e-400")]
    public void AcceptedNumberLexemesRoundTripWithoutDoubleCoercion(string lexeme)
    {
        var record = SyntheticTelemetry.Produce(extensions: SyntheticTelemetry.Json("{\"scribe\":{\"number\":" + lexeme + "}}"));
        var read = SyntheticTelemetry.Consume(SyntheticTelemetry.Wire(record));
        Assert.Equal(lexeme, read.Extensions.GetProperty("scribe").GetProperty("number").GetRawText());
    }

    [Fact]
    public void SixtyFourPropertiesAdmitSixtyFiveAndMergedOverflowReject()
    {
        static JsonElement Data(int count, string ns) => JsonSerializer.SerializeToElement(new Dictionary<string, object>
        { [ns] = Enumerable.Range(0, count).ToDictionary(i => "field" + i, i => (object)i) });
        var record = SyntheticTelemetry.Produce(extensions: Data(64, "scribe"));
        Assert.Equal(64, SyntheticTelemetry.Consume(SyntheticTelemetry.Wire(record)).Extensions.GetProperty("scribe").EnumerateObject().Count());
        Assert.Throws<ArgumentException>(() => SyntheticTelemetry.Produce(extensions: Data(65, "scribe")));
        var caller = SyntheticTelemetry.Produce(extensions: Data(64, "caller"));
        Assert.Throws<ArgumentException>(() => caller.WithCallerAttributes(SyntheticTelemetry.Json("{\"extra\":1}")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExtensionDepthCountsEveryContainerIncludingRoot(bool arrays)
    {
        static string Value(int depth, bool array)
        {
            var value = "0";
            for (var i = 2; i < depth; i++) value = array ? "[" + value + "]" : "{\"nested\":" + value + "}";
            return "{\"scribe\":{\"value\":" + value + "}}";
        }
        Assert.NotNull(SyntheticTelemetry.Consume(SyntheticTelemetry.Wire(SyntheticTelemetry.Produce(extensions: SyntheticTelemetry.Json(Value(8, arrays))))));
        Assert.Throws<ArgumentException>(() => SyntheticTelemetry.Produce(extensions: SyntheticTelemetry.Json(Value(9, arrays))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactExtensionByteBudgetIncludesNamespacesAndEscaping(bool unicode)
    {
        const string prefix = "{\"scribe\":{\"padding\":\"";
        const string suffix = "\"}}";
        var sample = unicode ? "你好" : "";
        var initial = SyntheticTelemetry.Json(prefix + sample + suffix);
        var overhead = JsonSerializer.SerializeToUtf8Bytes(initial).Length;
        var text = prefix + sample + new string('a', TelemetryLimits.MaximumExtensionBytes - overhead) + suffix;
        var element = SyntheticTelemetry.Json(text);
        Assert.Equal(TelemetryLimits.MaximumExtensionBytes, JsonSerializer.SerializeToUtf8Bytes(element).Length);
        var record = SyntheticTelemetry.Produce(extensions: element);
        Assert.NotNull(SyntheticTelemetry.Consume(SyntheticTelemetry.Wire(record)));
        var oversized = SyntheticTelemetry.Json(text.Insert(text.Length - suffix.Length, "a"));
        Assert.ThrowsAny<ArgumentException>(() => SyntheticTelemetry.Produce(extensions: oversized));
        var node = JsonNode.Parse(SyntheticTelemetry.Wire(record))!.AsObject();
        node["extensions"] = JsonNode.Parse(oversized.GetRawText());
        TelemetryContractTests.Rejected(node.ToJsonString(), TelemetryDiagnostic.ExtensionsTooLarge);
        if (unicode) Assert.NotEqual(text.Length, Encoding.UTF8.GetByteCount(text));
    }

    [Fact]
    public void FixedProductNamespaceAndAllUnicodeInputsAreValidated()
    {
        foreach (var product in new[] { "caller", "usage", "Bad", "bad namespace", "", "1bad" })
            Assert.Throws<ArgumentException>(() => SyntheticTelemetry.Produce(product: product, extensions: SyntheticTelemetry.Json("{}")));
        var json = SyntheticTelemetry.Wire(SyntheticTelemetry.Produce());
        var node = JsonNode.Parse(json)!.AsObject();
        node.Remove("extensions");
        var prefix = node.ToJsonString();
        TelemetryContractTests.Rejected(prefix[..^1] + ",\"extensions\":{\"scribe\":{\"text\":\"\\ud800\"}}}");
    }
}
