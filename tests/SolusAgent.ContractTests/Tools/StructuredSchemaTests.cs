using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CustomTools;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Tools;

public sealed class StructuredSchemaTests
{
    [Fact]
    public void NullableContainersAndBooleansRetainTheirNonNullConstraintsAndRootRemainsNonnullable()
    {
        const string source = """{"type":"object","properties":{"flag":{"type":["boolean","null"]},"row":{"type":["object","null"],"properties":{"v":{"type":"integer"}},"required":["v"],"additionalProperties":false},"values":{"type":["array","null"],"items":{"type":"string"},"minItems":1,"maxItems":2}},"required":["flag","row","values"],"additionalProperties":false}""";
        var schema = ToolSchema.Parse(source);
        Assert.Equal(ToolError.None, schema.Validate("""{"flag":null,"row":null,"values":null}"""));
        Assert.Equal(ToolError.None, schema.Validate("""{"flag":true,"row":{"v":1e0},"values":["x"]}"""));
        Assert.Equal(ToolError.InvalidArguments, schema.Validate("""{"flag":"true","row":null,"values":null}"""));
        Assert.Equal(ToolError.InvalidArguments, schema.Validate("""{"flag":false,"row":{},"values":["x"]}"""));
        Assert.Equal(ToolError.InvalidArguments, schema.Validate("""{"flag":false,"row":null,"values":[]}"""));
        Assert.Equal(ToolError.InvalidArguments, schema.Validate("null"));
        Assert.Equal(ToolError.UnsupportedSchema, Assert.Throws<ToolContractException>(() => ToolSchema.Parse(
            source.Replace("\"type\":\"object\"", "\"type\":[\"object\",\"null\"]", StringComparison.Ordinal))).Error);
    }

    [Theory]
    [InlineData("list_files")] [InlineData("list_changed_files")] [InlineData("read_file")]
    [InlineData("read_diff")] [InlineData("search_text")]
    public async Task FiveActualNonterminalShapesPassRealPreparationInvocationAndHistoricalAdmission(string name)
    {
        var tool = new StructuredReadTool(name); var capability = new StructuredReadCapability();
        var call = new ToolCall("Original-" + name, name, StructuredReadFixtures.Arguments(name));
        var prepared = tool.Prepare(call); Assert.True(prepared.Accepted); Assert.Equal(0, capability.Entries);
        Assert.Equal(ToolError.None, tool.ValidateInvocation(prepared.Prepared!, call, capability));
        var result = await tool.InvokeAsync(prepared.Prepared!, call, capability);
        Assert.Equal(ToolOutcome.Succeeded, result.Outcome); Assert.Equal(1, capability.Entries);
        Assert.Equal(StructuredReadFixtures.Result(name), result.Json);
        Assert.Equal(name == "read_file" ? 100 : name == "read_diff" ? 1 : 0, capability.LastCount);
        var historical = ToolResult.RestoreHistorical(tool.Descriptor, call, result.Call, result.Outcome, result.Error, result.InvocationStarted, result.Json);
        Assert.True(historical.IsHistorical); Assert.Equal(call.ArgumentsJson, historical.Call.ArgumentsJson); Assert.Equal(result.Json, historical.Json);
        Assert.Equal(1, capability.Entries);
        foreach (var schema in new[] { tool.Descriptor.InputSchema, tool.Descriptor.ResultSchema })
        {
            Assert.Equal("bounded_closed_object", schema.Profile);
            Assert.Equal(schema.NormalizedJson, ToolSchema.Parse(schema.NormalizedJson).NormalizedJson);
        }
    }

    [Fact]
    public void CompleteResultsExerciseFormerMemberAndDepthFailuresWithinSelectedBytes()
    {
        var file = StructuredReadFixtures.Result("read_file");
        using var read = JsonDocument.Parse(file);
        Assert.Equal(100, read.RootElement.GetProperty("lines").GetArrayLength());
        Assert.Equal(216, Members(read.RootElement)); Assert.InRange(Encoding.UTF8.GetByteCount(file), 1, 4096);
        Assert.Equal(ToolError.None, ToolSchema.Parse(StructuredReadFixtures.ResultSchema("read_file")).Validate(file, 4096));
        var diff = StructuredReadFixtures.Result("read_diff");
        using var document = JsonDocument.Parse(diff);
        Assert.Equal(13, document.RootElement.EnumerateObject().Count()); Assert.Equal(5, Depth(document.RootElement));
        Assert.Equal(ToolError.None, ToolSchema.Parse(StructuredReadFixtures.ResultSchema("read_diff")).Validate(diff, 4096));
        Assert.Equal(ToolError.LimitExceeded, ToolSchema.Parse(StructuredReadFixtures.ResultSchema("read_file"))
            .Validate(StructuredReadFixtures.Result("read_file", 800), 4096));
    }

    [Fact]
    public void RequiredNullableAndOptionalNonnullableFieldsRemainDistinct()
    {
        var input = ToolSchema.Parse(StructuredReadFixtures.InputSchema("list_files"));
        Assert.Equal(ToolError.None, input.Validate("{}"));
        Assert.Equal(ToolError.InvalidArguments, input.Validate("""{"prefix":null}"""));
        var omitted = StructuredReadFixtures.InputSchema("list_files").Replace(",\"required\":[]", "", StringComparison.Ordinal);
        Assert.Equal(input.NormalizedJson, ToolSchema.Parse(omitted).NormalizedJson);
        var schema = ToolSchema.Parse(StructuredReadFixtures.ResultSchema("read_file"));
        var result = JsonNode.Parse(StructuredReadFixtures.Result("read_file"))!;
        result["returned_start_line"] = null; result["returned_end_line"] = null; result["lines"] = new JsonArray();
        Assert.Equal(ToolError.None, schema.Validate(result.ToJsonString()));
        result.AsObject().Remove("returned_start_line");
        Assert.Equal(ToolError.InvalidArguments, schema.Validate(result.ToJsonString()));
    }

    [Theory]
    [InlineData("range")] [InlineData("enum")] [InlineData("unknown")] [InlineData("missing")]
    [InlineData("item")] [InlineData("null")] [InlineData("count")]
    public async Task MalformedNestedOutputsRejectAfterHonestEntryAndHistoricalFactoryAlsoRefuses(string mutation)
    {
        var data = JsonNode.Parse(StructuredReadFixtures.Result("read_file"))!;
        switch (mutation)
        {
            case "range": data["lines"]![0]!["line"] = 0; break;
            case "enum": data["status"] = "invented"; break;
            case "unknown": data["reviewed_identity"]!["permission"] = true; break;
            case "missing": data["lines"]![0]!.AsObject().Remove("text"); break;
            case "item": data["lines"]![0] = "string-workaround"; break;
            case "null": data["lines"] = null; break;
            case "count": data["requested_line_count"] = 801; break;
        }
        var json = data.ToJsonString(); var tool = new StructuredReadTool("read_file", result: json);
        var call = new ToolCall("id", "read_file", StructuredReadFixtures.Arguments("read_file")); var cap = new StructuredReadCapability();
        var result = await tool.InvokeAsync(tool.Prepare(call).Prepared!, call, cap);
        Assert.Equal(ToolOutcome.Rejected, result.Outcome); Assert.Equal(ToolError.InvalidResult, result.Error);
        Assert.True(result.InvocationStarted); Assert.Equal(1, cap.Entries); Assert.Null(result.Json);
        Assert.Throws<ToolContractException>(() => ToolResult.RestoreHistorical(tool.Descriptor, call, call, ToolOutcome.Succeeded, ToolError.None, true, json));
    }

    [Theory]
    [InlineData("read_file", "start_line", "0")] [InlineData("read_file", "start_line", "2147483648")]
    [InlineData("read_file", "line_count", "801")] [InlineData("read_diff", "hunk_count", "21")]
    [InlineData("read_file", "line_count", "1.00000000000000000001")]
    public void RegisteredInputRangesAndExactNumbersRejectBeforeEffects(string name, string field, string token)
    {
        var json = "{\"path\":\"sample.cs\",\"" + field + "\":" + token + "}";
        var tool = new StructuredReadTool(name); Assert.False(tool.Prepare(new("id", name, json)).Accepted);
    }

    [Fact]
    public void StringEnumsApplyToNullAndNormalizationPreservesAllConstraints()
    {
        const string first = """{"type":"object","properties":{"v":{"type":["null","string"],"enum":[null,"z","a"]}},"required":["v"],"additionalProperties":false}""";
        const string second = """{"additionalProperties":false,"required":["v"],"properties":{"v":{"enum":["a",null,"z"],"type":["string","null"]}},"type":"object"}""";
        var schema = ToolSchema.Parse(first); Assert.Equal(schema.NormalizedJson, ToolSchema.Parse(second).NormalizedJson);
        foreach (var value in new[] { "null", "\"a\"", "\"z\"" }) Assert.Equal(ToolError.None, schema.Validate("{\"v\":" + value + "}"));
        Assert.Equal(ToolError.InvalidArguments, schema.Validate("""{"v":"other"}"""));
        var withoutNull = ToolSchema.Parse(first.Replace("null,\"z\"", "\"z\"", StringComparison.Ordinal));
        Assert.Equal(ToolError.InvalidArguments, withoutNull.Validate("""{"v":null}"""));
        var integer = ToolSchema.Parse("""{"type":"object","properties":{"n":{"type":"integer","minimum":1.0,"maximum":1e2}},"required":["n"],"additionalProperties":false}""");
        using var normalized = JsonDocument.Parse(integer.NormalizedJson);
        Assert.Equal(1, normalized.RootElement.GetProperty("properties").GetProperty("n").GetProperty("minimum").GetInt64());
        Assert.Equal(100, normalized.RootElement.GetProperty("properties").GetProperty("n").GetProperty("maximum").GetInt64());
        Assert.Equal(ToolError.None, ToolSchema.Parse(integer.NormalizedJson).Validate("""{"n":1.5e1}"""));
        Assert.Equal(ToolError.InvalidArguments, integer.Validate("""{"n":101}"""));
    }

    [Theory]
    [InlineData("""{"type":"string","minimum":1}""")]
    [InlineData("""{"type":"integer","enum":[1]}""")]
    [InlineData("""{"type":"integer","minimum":2,"maximum":1}""")]
    [InlineData("""{"type":"integer","minimum":1.1}""")]
    [InlineData("""{"type":"array","items":{"type":"string"},"minItems":2,"maxItems":1}""")]
    [InlineData("""{"type":"array","items":{"type":"string"},"maxItems":1001}""")]
    [InlineData("""{"type":"string","enum":[]}""")]
    [InlineData("""{"type":"string","enum":["a","\u0061"]}""")]
    [InlineData("""{"type":"string","enum":[null]}""")]
    [InlineData("""{"type":["string","null","integer"]}""")]
    [InlineData("""{"type":["string","string"]}""")]
    [InlineData("""{"type":"null"}""")]
    [InlineData("""{"type":"boolean","properties":{}}""")]
    [InlineData("""{"type":"object","properties":{},"additionalProperties":true}""")]
    [InlineData("""{"type":"object","properties":{},"additionalProperties":false,"$ref":"x"}""")]
    public void UnsupportedOrIncoherentNestedSchemaSemanticsRejectWithoutDroppingKeywords(string node)
    {
        var json = "{\"type\":\"object\",\"properties\":{\"v\":" + node + "},\"required\":[],\"additionalProperties\":false}";
        Assert.Equal(ToolError.UnsupportedSchema, Assert.Throws<ToolContractException>(() => ToolSchema.Parse(json)).Error);
    }

    [Theory]
    [InlineData("""{"lines":[{"line":1,"l\u0069ne":2,"text":"x"}]}""", ToolError.InvalidJson)]
    [InlineData("""{"lines":[{"line":1,"text":"\ud800"}]}""", ToolError.InvalidEncoding)]
    [InlineData("""{"lines":[{"line":1,"text":"x",}]}""", ToolError.InvalidJson)]
    public void NestedDuplicatesEncodingAndSyntaxRemainStrict(string payload, ToolError expected)
    {
        var schema = ToolSchema.Parse("""{"type":"object","properties":{"lines":{"type":"array","items":{"type":"object","properties":{"line":{"type":"integer"},"text":{"type":"string"}},"required":["line","text"],"additionalProperties":false}}},"required":["lines"],"additionalProperties":false}""");
        Assert.Equal(expected, schema.Validate(payload));
    }

    private static int Members(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Sum(p => 1 + Members(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().Sum(Members), _ => 0,
    };
    private static int Depth(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => 1 + value.EnumerateObject().Select(p => Depth(p.Value)).DefaultIfEmpty().Max(),
        JsonValueKind.Array => 1 + value.EnumerateArray().Select(Depth).DefaultIfEmpty().Max(), _ => 0,
    };
}
