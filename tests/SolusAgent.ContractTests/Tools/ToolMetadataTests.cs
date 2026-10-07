using System.Text;
using CustomTools;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Tools;

/// <summary>Meaningful metadata/schema boundaries, including the two corrected Plan findings.</summary>
public sealed class ToolMetadataTests
{
    private static ToolDescriptor Descriptor(string name = "counter", string description = "Counter", string capability = "counter_increment") =>
        new(name, description, ToolSchema.Parse(CounterTool.InputSchema), ToolSchema.Parse(CounterTool.ResultSchema), capability, ToolEffect.Mutating);

    [Theory]
    [InlineData("")]
    [InlineData("Counter")]
    [InlineData("écho")]
    [InlineData(" counter")]
    [InlineData("counter ")]
    [InlineData("counter-name")]
    [InlineData("counter\0")]
    [InlineData("1counter")]
    public void NonCanonicalToolAndCapabilityIdentitiesRejectWithoutFolding(string identity)
    {
        AssertError(ToolError.InvalidMetadata, () => Descriptor(name: identity));
        AssertError(ToolError.InvalidMetadata, () => Descriptor(capability: identity));
        AssertError(ToolError.InvalidMetadata, () => new ToolCall("id", identity, "{}"));
    }

    [Fact]
    public void CanonicalIdentityBoundsAreExact()
    {
        var exact = "a" + new string('_', ToolLimits.IdentifierCharacters - 1);
        Assert.Equal(exact, Descriptor(name: exact, capability: exact).Name);
        Assert.Equal(exact, Descriptor(name: exact, capability: exact).CapabilityId);
        AssertError(ToolError.InvalidMetadata, () => Descriptor(name: exact + "a"));
        AssertError(ToolError.InvalidMetadata, () => Descriptor(capability: exact + "a"));
        AssertError(ToolError.UnsupportedCapability, () => new CounterTool(effect: (ToolEffect)99));
    }

    [Fact]
    public void DescriptionNormalizationAndUtf8BoundAreExecutable()
    {
        Assert.Equal("Counter", Descriptor(description: " \tCounter\n ").Description);
        Assert.Equal(new string('x', ToolLimits.DescriptionBytes), Descriptor(description: new string('x', ToolLimits.DescriptionBytes)).Description);
        Assert.Equal(new string('é', ToolLimits.DescriptionBytes / 2), Descriptor(description: new string('é', ToolLimits.DescriptionBytes / 2)).Description);
        AssertError(ToolError.LimitExceeded, () => Descriptor(description: new string('x', ToolLimits.DescriptionBytes + 1)));
        AssertError(ToolError.LimitExceeded, () => Descriptor(description: new string('é', ToolLimits.DescriptionBytes / 2) + "x"));
        AssertError(ToolError.InvalidEncoding, () => Descriptor(description: "\ud800"));
        AssertError(ToolError.InvalidMetadata, () => Descriptor(description: " \t\n"));
        // Source admission happens before Trim can allocate an unbounded normalized copy.
        AssertError(ToolError.LimitExceeded, () => Descriptor(description: new string(' ', ToolLimits.DescriptionBytes) + "x"));
    }

    [Fact]
    public void CallIdsAreOpaqueExactAndBoundedInActualPreparedState()
    {
        var tool = new CounterTool();
        foreach (var id in new[] { new string('A', ToolLimits.CallIdBytes), new string('é', ToolLimits.CallIdBytes / 2), " Case-Sensitive " })
        {
            var call = new ToolCall(id, "counter", "{\"amount\":1}");
            Assert.Equal(id, tool.Prepare(call).Prepared!.Call.CallId);
        }
        Assert.False(new ToolCall("A", "counter", "{}").Matches(new("a", "counter", "{}")));
        AssertError(ToolError.LimitExceeded, () => new ToolCall(new string('A', ToolLimits.CallIdBytes + 1), "counter", "{}"));
        AssertError(ToolError.LimitExceeded, () => new ToolCall(new string('é', ToolLimits.CallIdBytes / 2) + "x", "counter", "{}"));
        AssertError(ToolError.InvalidMetadata, () => new ToolCall("", "counter", "{}"));
        AssertError(ToolError.InvalidMetadata, () => new ToolCall("a\nb", "counter", "{}"));
        AssertError(ToolError.InvalidEncoding, () => new ToolCall("\ud800", "counter", "{}"));
    }

    [Fact]
    public void ByteAndStringAdmissionHappensBeforeParsingAndOwnsImmutableData()
    {
        const string prefix = "{\"amount\":1}";
        var exact = prefix + new string(' ', ToolLimits.PayloadBytes - prefix.Length);
        Assert.True(new CounterTool().Prepare(ToolCall.FromUtf8("id", "counter", Encoding.UTF8.GetBytes(exact))).Accepted);
        AssertError(ToolError.LimitExceeded, () => ToolCall.FromUtf8("id", "counter", Encoding.UTF8.GetBytes(exact + " ")));
        AssertError(ToolError.LimitExceeded, () => new ToolCall("id", "counter", exact + " "));
        AssertError(ToolError.InvalidEncoding, () => ToolCall.FromUtf8("id", "counter", [0xC3, 0x28]));
        AssertError(ToolError.InvalidEncoding, () => new ToolCall("id", "counter", "\udfff"));
        AssertError(ToolError.InvalidEncoding, () => ToolSchema.FromUtf8([0xFF]));
    }

    [Fact]
    public void SupportedSchemaNormalizationPreservesPropertyTypesAndRequiredOrdering()
    {
        var first = ToolSchema.Parse("{\"type\":\"object\",\"properties\":{\"b\":{\"type\":\"boolean\"},\"a\":{\"type\":\"string\"}},\"required\":[\"b\",\"a\"],\"additionalProperties\":false}");
        var second = ToolSchema.Parse("{\"additionalProperties\":false,\"required\":[\"a\",\"b\"],\"properties\":{\"a\":{\"type\":\"string\"},\"b\":{\"type\":\"boolean\"}},\"type\":\"object\"}");
        Assert.Equal(first.NormalizedJson, second.NormalizedJson);
        Assert.Equal("closed_scalar_object", first.Profile);
        Assert.Equal(ToolError.None, first.Validate("{\"b\":true,\"a\":\"text\"}"));
        Assert.Equal(ToolError.None, first.Validate("{\"b\":false,\"a\":\"\\ud83d\\ude00\"}"));
        Assert.Equal(ToolError.None, first.Validate("{\"b\":true,\"a\":\"\\\\ud800\"}"));
        Assert.Equal(ToolError.InvalidArguments, first.Validate("{\"b\":\"true\",\"a\":\"text\"}"));
        Assert.Equal(ToolError.InvalidArguments, first.Validate("{\"a\":\"text\"}"));
        Assert.Equal(ToolError.InvalidEncoding, first.Validate("{\"b\":true,\"a\":\"\\ud800\"}"));
        Assert.Equal(ToolError.InvalidEncoding, first.Validate("{\"b\":true,\"a\":\"\\udc00\"}"));
        Assert.Equal(ToolError.InvalidEncoding, first.Validate("{\"b\":true,\"a\":\"\\ud800x\"}"));
    }

    [Theory]
    [InlineData("{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":true}")]
    [InlineData("{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false,\"title\":\"ignored?\"}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"string\",\"minLength\":1}},\"required\":[],\"additionalProperties\":false}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"}},\"required\":[],\"additionalProperties\":false}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"array\"}},\"required\":[],\"additionalProperties\":false}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"object\"}},\"required\":[],\"additionalProperties\":false}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"x\":{\"type\":[\"string\",\"null\"]}},\"required\":[],\"additionalProperties\":false}")]
    [InlineData("{\"type\":\"object\",\"properties\":{},\"required\":[\"missing\"],\"additionalProperties\":false}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"string\"}},\"required\":[\"x\",\"x\"],\"additionalProperties\":false}")]
    [InlineData("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
    [InlineData("{\"$ref\":\"external\"}")]
    public void UnsupportedSchemaSemanticsNeverDisappearDuringNormalization(string json) =>
        AssertError(ToolError.UnsupportedSchema, () => ToolSchema.Parse(json));

    [Theory]
    [InlineData("{\"type\":\"object\",\"type\":\"string\"}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"string\",\"type\":\"integer\"}},\"required\":[],\"additionalProperties\":false}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"string\"},\"x\":{\"type\":\"integer\"}},\"required\":[],\"additionalProperties\":false}")]
    [InlineData("{} {}")]
    [InlineData("//comment\n{}")]
    [InlineData("{\"bad\":}")]
    public void AmbiguousOrMalformedSchemaJsonRejects(string json) => AssertError(ToolError.InvalidJson, () => ToolSchema.Parse(json));

    [Theory]
    [InlineData("9223372036854775807", ToolError.None)]
    [InlineData("-9223372036854775808", ToolError.None)]
    [InlineData("-0", ToolError.None)]
    [InlineData("9223372036854775808", ToolError.InvalidArguments)]
    [InlineData("-9223372036854775809", ToolError.InvalidArguments)]
    [InlineData("1.00", ToolError.InvalidArguments)]
    [InlineData("1e0", ToolError.InvalidArguments)]
    [InlineData("1e999999999999", ToolError.InvalidArguments)]
    public void IntegerSemanticsAreLexicalInt64WithoutCoercion(string number, ToolError expected) =>
        Assert.Equal(expected, ToolSchema.Parse(CounterTool.InputSchema).Validate("{\"amount\":" + number + "}"));

    [Fact]
    public void SchemaSourceSizePropertyCountAndMultibyteArgumentLimitsAreReal()
    {
        var schema = CounterTool.InputSchema;
        var exact = schema + new string(' ', ToolLimits.SchemaBytes - schema.Length);
        Assert.Equal(ToolSchema.Parse(schema).NormalizedJson, ToolSchema.FromUtf8(Encoding.UTF8.GetBytes(exact)).NormalizedJson);
        AssertError(ToolError.LimitExceeded, () => ToolSchema.Parse(exact + " "));
        var properties = string.Join(',', Enumerable.Range(0, ToolLimits.Properties).Select(i => $"\"p{i}\":{{\"type\":\"string\"}}"));
        var thirtyTwo = "{\"type\":\"object\",\"properties\":{" + properties + "},\"required\":[],\"additionalProperties\":false}";
        var parsed = ToolSchema.Parse(thirtyTwo);
        Assert.Equal(ToolError.None, parsed.Validate("{}"));
        AssertError(ToolError.UnsupportedSchema, () => ToolSchema.Parse(thirtyTwo.Replace("},\"required\"", ",\"more\":{\"type\":\"string\"}},\"required\"")));
        const string stringSchema = "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}},\"required\":[\"text\"],\"additionalProperties\":false}";
        var payload = "{\"text\":\"é\"}";
        var bytes = Encoding.UTF8.GetByteCount(payload);
        Assert.Equal(ToolError.None, ToolSchema.Parse(stringSchema).Validate(payload, bytes));
        Assert.Equal(ToolError.LimitExceeded, ToolSchema.Parse(stringSchema).Validate(payload, bytes - 1));
    }

    private static void AssertError(ToolError error, Action action) => Assert.Equal(error, Assert.Throws<ToolContractException>(action).Error);
}
