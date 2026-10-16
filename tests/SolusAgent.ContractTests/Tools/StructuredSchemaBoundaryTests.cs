using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CustomTools;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Tools;

public sealed class StructuredSchemaBoundaryTests
{
    [Fact]
    public void AggregateMembersCountOccurrencesAcrossRowsButDuplicateNamesOnlyWithinEachObject()
    {
        var fields = Enumerable.Range(0, 32).ToDictionary(i => "p" + i, _ => (object?)new { type = "integer" });
        var schema = Parse(new() { ["rows"] = Array(Object(fields)) });
        Dictionary<string, int> Row(int count) => Enumerable.Range(0, count).ToDictionary(i => "p" + i, _ => 0);
        var rows = Enumerable.Range(0, 63).Select(_ => Row(32)).Append(Row(31)).ToArray();
        var exact = JsonSerializer.Serialize(new { rows });
        Assert.InRange(Encoding.UTF8.GetByteCount(exact), 1, ToolLimits.PayloadBytes);
        Assert.Equal(ToolError.None, schema.Validate(exact)); // 1 root member + 63*32 + 31 = 2048.
        rows[^1]["p31"] = 0;
        Assert.Equal(ToolError.LimitExceeded, schema.Validate(JsonSerializer.Serialize(new { rows })));
    }

    [Fact]
    public void PerArrayAndAggregateElementsIncludeNullsAndNestedContainersOnceEach()
    {
        var integer = new { type = "integer" };
        var nullable = new { type = new[] { "integer", "null" } };
        var schema = Parse(new() { ["a"] = Array(integer), ["b"] = Array(nullable) });
        var a = Enumerable.Repeat(0, 1000).ToArray();
        Assert.Equal(ToolError.None, schema.Validate(JsonSerializer.Serialize(new { a, b = new int?[24] })));
        Assert.Equal(ToolError.LimitExceeded, schema.Validate(JsonSerializer.Serialize(new { a, b = new int?[25] })));
        Assert.Equal(ToolError.LimitExceeded, schema.Validate(JsonSerializer.Serialize(new { a = new int[1001] })));
        var nested = Parse(new() { ["a"] = Array(Array(integer)) });
        Assert.Equal(ToolError.None, nested.Validate(JsonSerializer.Serialize(new { a = new[] { new int[511], new int[511] } })));
        Assert.Equal(ToolError.LimitExceeded, nested.Validate(JsonSerializer.Serialize(new { a = new[] { new int[511], new int[512] } })));
    }

    [Fact]
    public void PayloadAndSemanticSchemaDepthAreSeparateFromSyntaxDepth()
    {
        object node = new { type = "integer" };
        for (var i = 0; i < 5; i++) node = Array(node);
        var exact = Parse(new() { ["a"] = node });
        Assert.Equal(ToolError.None, exact.Validate("""{"a":[[[[[0]]]]]}"""));
        Assert.Equal(ToolError.InvalidJson, exact.Validate("""{"a":[[[[[[0]]]]]]}"""));
        Error(ToolError.UnsupportedSchema, () => Parse(new() { ["a"] = Array(node) }));
        // Semantic rejection follows successful syntax admission at 18; at 19 the syntax reader rejects first.
        Error(ToolError.UnsupportedSchema, () => ToolSchema.Parse(NestArrays(ToolLimits.SchemaJsonDepth)));
        Error(ToolError.InvalidJson, () => ToolSchema.Parse(NestArrays(ToolLimits.SchemaJsonDepth + 1)));
    }

    [Fact]
    public void SchemaSyntaxMemberAndElementBudgetsHaveIndependentReaderWitnesses()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        string Document(int count) => "{" + string.Join(',', Enumerable.Range(0, count)
            .Select(i => "\"" + alphabet[i / alphabet.Length] + alphabet[i % alphabet.Length] + "\":0")) + "}";
        var exact = Document(ToolLimits.SchemaJsonProperties);
        Assert.InRange(Encoding.UTF8.GetByteCount(exact), 1, ToolLimits.SchemaBytes);
        Error(ToolError.UnsupportedSchema, () => ToolSchema.Parse(exact)); // Syntax passed; no type was declared.
        Error(ToolError.LimitExceeded, () => ToolSchema.Parse(Document(ToolLimits.SchemaJsonProperties + 1)));
        string Elements(int count) => "[" + string.Join(',', Enumerable.Repeat("0", count)) + "]";
        Error(ToolError.UnsupportedSchema, () => ToolSchema.Parse(Elements(ToolLimits.SchemaJsonElements)));
        Error(ToolError.LimitExceeded, () => ToolSchema.Parse(Elements(ToolLimits.SchemaJsonElements + 1)));
    }

    [Fact]
    public void DeclarationAndNodeCountsDoNotConflateObjectsItemsAndSyntaxMembers()
    {
        var groups = Enumerable.Range(0, 4).ToDictionary(g => "g" + g, _ => (object?)Object(
            Enumerable.Range(0, 31).ToDictionary(i => "p" + i, _ => (object?)new { type = "string" })));
        Assert.Equal(ToolError.None, Parse(groups).Validate("{}")); // 4 + 4*31 = 128 declarations.
        var tooMany = JsonNode.Parse(JsonSerializer.Serialize(Object(groups)))!;
        tooMany["properties"]!["g0"]!["properties"]!["p31"] = JsonSerializer.SerializeToNode(new { type = "string" });
        Error(ToolError.UnsupportedSchema, () => ToolSchema.Parse(tooMany.ToJsonString()));

        var nodeGroups = Enumerable.Range(0, 3).ToDictionary(g => "g" + g, _ => (object?)Object(
            Enumerable.Range(0, 28).ToDictionary(i => "p" + i, _ => (object?)Array(Array(new { type = "string" })))));
        var source = JsonSerializer.Serialize(Object(nodeGroups));
        Assert.InRange(Encoding.UTF8.GetByteCount(source), 1, ToolLimits.SchemaBytes);
        // 256 nodes pass the node guard, but emitting intrinsic array bounds exceeds normalized bytes.
        Error(ToolError.LimitExceeded, () => ToolSchema.Parse(source));
        var plusOne = JsonNode.Parse(source)!;
        plusOne["properties"]!["g0"]!["properties"]!["p0"]!["items"]!["items"] =
            JsonSerializer.SerializeToNode(Array(new { type = "string" }));
        Error(ToolError.UnsupportedSchema, () => ToolSchema.Parse(plusOne.ToJsonString()));
    }

    [Fact]
    public void EnumAndNormalizedSourceByteBoundsAreReal()
    {
        object Enum(int count) => new { type = "string", @enum = Enumerable.Range(0, count).Select(i => "v" + i).ToArray() };
        Assert.Equal(ToolError.None, Parse(new() { ["v"] = Enum(32) }).Validate("""{"v":"v31"}"""));
        Error(ToolError.UnsupportedSchema, () => Parse(new() { ["v"] = Enum(33) }));
        const string prefix = """{"type":"object","properties":{"v":{"type":"string","enum":[""]}},"required":[],"additionalProperties":false}""";
        var baseSize = Encoding.UTF8.GetByteCount(ToolSchema.Parse(prefix).NormalizedJson);
        var exact = prefix.Replace("[\"\"]", "[\"" + new string('x', ToolLimits.SchemaBytes - baseSize) + "\"]", StringComparison.Ordinal);
        Assert.Equal(ToolLimits.SchemaBytes, Encoding.UTF8.GetByteCount(ToolSchema.Parse(exact).NormalizedJson));
        Error(ToolError.LimitExceeded, () => ToolSchema.Parse(exact + " "));
        var expansion = prefix.Replace("[\"\"]", "[\"" + new string('é', 1500) + "\"]", StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(expansion) < ToolLimits.SchemaBytes);
        Error(ToolError.LimitExceeded, () => ToolSchema.Parse(expansion));
    }

    [Fact]
    public async Task DescriptorBytesIncludeCompleteMetadataAndActualOutputGuardDoesNotTruncate()
    {
        var raw = StructuredReadFixtures.Result("read_file");
        var exact = raw + new string(' ', 4096 - Encoding.UTF8.GetByteCount(raw));
        var tool = new StructuredReadTool("read_file", result: exact);
        Assert.Equal(ToolError.None, tool.Descriptor.ResultSchema.Validate(exact, 4096));
        Assert.Equal(ToolError.LimitExceeded, tool.Descriptor.ResultSchema.Validate(exact + " ", 4096));
        var call = new ToolCall("id", "read_file", StructuredReadFixtures.Arguments("read_file")); var cap = new StructuredReadCapability();
        var output = await tool.InvokeAsync(tool.Prepare(call).Prepared!, call, cap);
        Assert.Equal(ToolOutcome.Succeeded, output.Outcome); Assert.Equal(exact, output.Json);
        var over = new StructuredReadTool("read_file", result: exact + " ");
        var rejected = await over.InvokeAsync(over.Prepare(call).Prepared!, call, cap);
        Assert.Equal(ToolError.InvalidResult, rejected.Error); Assert.True(rejected.InvocationStarted); Assert.Null(rejected.Json);
        Assert.Equal(2, cap.Entries);
        var stringSchema = Parse(new() { ["v"] = new { type = "string" } });
        const string multibyte = """{"v":"é"}""";
        Assert.Equal(ToolError.None, stringSchema.Validate(multibyte, Encoding.UTF8.GetByteCount(multibyte)));
        Assert.Equal(ToolError.LimitExceeded, stringSchema.Validate(multibyte, Encoding.UTF8.GetByteCount(multibyte) - 1));
    }

    private static object Object(Dictionary<string, object?> properties) => new { type = "object", properties, required = System.Array.Empty<string>(), additionalProperties = false };
    private static object Array(object items) => new { type = "array", items };
    private static ToolSchema Parse(Dictionary<string, object?> properties) => ToolSchema.Parse(JsonSerializer.Serialize(Object(properties)));
    private static string NestArrays(int depth) => new string('[', depth) + "0" + new string(']', depth);
    private static void Error(ToolError error, Action action) => Assert.Equal(error, Assert.Throws<ToolContractException>(action).Error);
}
