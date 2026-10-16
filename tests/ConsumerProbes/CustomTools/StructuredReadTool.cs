using System.Text.Json;
using SolusAgent.Tools.Api;

namespace CustomTools;

/// <summary>Independently authored synthetic shapes for five nonterminal read-tool families; no product authority or I/O.</summary>
public static class StructuredReadFixtures
{
    /// <summary>The five independently described nonterminal shapes.</summary>
    public static IReadOnlyList<string> Names { get; } = System.Array.AsReadOnly(new[] { "list_files", "list_changed_files", "read_file", "read_diff", "search_text" });

    /// <summary>Actual registered input constraints, including the two optional-only objects.</summary>
    public static string InputSchema(string name) => JsonSerializer.Serialize(name switch
    {
        "list_files" => Object(new() { ["prefix"] = String(), ["after"] = String() }, []),
        "list_changed_files" => Object(new() { ["after"] = String() }, []),
        "read_file" => Object(new() { ["path"] = String(), ["start_line"] = Integer(1, int.MaxValue), ["line_count"] = Integer(1, 800) }, ["path"]),
        "read_diff" => Object(new() { ["path"] = String(), ["start_hunk"] = Integer(1, int.MaxValue), ["hunk_count"] = Integer(1, 20) }, ["path"]),
        "search_text" => Object(new() { ["query"] = String(), ["path"] = String() }, ["query"]),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    });

    /// <summary>Complete closed success shapes with native nested data, nullable fields, ranges and nonterminal enums.</summary>
    public static string ResultSchema(string name)
    {
        var properties = new Dictionary<string, object?> { ["status"] = String("ok"), ["reviewed_identity"] = IdentitySchema(),
            ["observation_id"] = String() };
        switch (name)
        {
            case "list_files":
                properties["prefix"] = String(nullable: true); properties["after"] = String(nullable: true);
                properties["paths"] = Array(String(), 100); properties["truncated"] = Boolean(); properties["next_after"] = String(nullable: true);
                break;
            case "list_changed_files":
                properties["after"] = String(nullable: true);
                properties["changes"] = Array(Object(new()
                {
                    ["path"] = String(), ["previous_path"] = String(nullable: true),
                    ["status"] = Strings(["added", "removed", "modified", "changed", "renamed", "copied"]),
                    ["additions"] = Integer(0, int.MaxValue), ["deletions"] = Integer(0, int.MaxValue), ["changes"] = Integer(0, int.MaxValue),
                    ["patch_status"] = Strings(["available", "unavailable", "binary"]), ["patch_sha256"] = String(nullable: true), ["source_truncated"] = Boolean(),
                }), 100);
                properties["truncated"] = Boolean(); properties["next_after"] = String(nullable: true);
                break;
            case "read_file":
                properties["status"] = Strings(["ok", "start_after_eof"]); properties["path"] = String(); properties["raw_sha256"] = String();
                properties["requested_start_line"] = Integer(1, int.MaxValue); properties["requested_line_count"] = Integer(1, 800);
                properties["returned_start_line"] = Integer(1, int.MaxValue, true); properties["returned_end_line"] = Integer(1, int.MaxValue, true);
                properties["lines"] = Array(Object(new() { ["line"] = Integer(1, int.MaxValue), ["text"] = String() }), 800);
                properties["truncated"] = Boolean(); properties["truncation_reason"] = Strings(["line_count", "result_bytes", null], true);
                break;
            case "read_diff":
                properties["status"] = Strings(["ok", "empty", "eof", "unavailable", "binary"]); properties["path"] = String();
                properties["patch_sha256"] = String(nullable: true); properties["source_truncated"] = Boolean();
                properties["requested_start_hunk"] = Integer(1, int.MaxValue); properties["requested_hunk_count"] = Integer(1, 20);
                properties["returned_start_hunk"] = Integer(1, int.MaxValue, true); properties["returned_end_hunk"] = Integer(1, int.MaxValue, true);
                properties["hunks"] = Array(Object(new()
                {
                    ["old_start"] = Integer(0, int.MaxValue), ["old_count"] = Integer(0, int.MaxValue),
                    ["new_start"] = Integer(0, int.MaxValue), ["new_count"] = Integer(0, int.MaxValue),
                    ["lines"] = Array(Object(new() { ["kind"] = Strings(["context", "addition", "deletion", "no_newline"]),
                        ["old_line"] = Integer(1, int.MaxValue, true), ["new_line"] = Integer(1, int.MaxValue, true), ["text"] = String() }), 1000),
                }), 20);
                properties["truncated"] = Boolean(); properties["next_start_hunk"] = Integer(1, int.MaxValue, true);
                break;
            case "search_text":
                properties["query_sha256"] = String(); properties["path"] = String(nullable: true);
                properties["files_scanned"] = Integer(0, 100); properties["raw_bytes_scanned"] = Integer(0, long.MaxValue);
                foreach (var key in new[] { "skipped_invalid_utf8", "skipped_binary", "skipped_lone_cr", "skipped_oversized" })
                    properties[key] = Integer(0, int.MaxValue);
                properties["matches"] = Array(Object(new() { ["path"] = String(), ["raw_sha256"] = String(),
                    ["line"] = Integer(1, int.MaxValue), ["text"] = String() }), 100);
                properties["truncated"] = Boolean(); properties["truncation_reason"] = Strings(["files_scanned", "bytes_scanned", "matches", "result_bytes", null], true);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(name));
        }
        return JsonSerializer.Serialize(Object(properties));
    }

    /// <summary>Original synthetic raw arguments, including exact integral decimal/exponent spellings.</summary>
    public static string Arguments(string name) => name switch
    {
        "list_files" or "list_changed_files" => "{}",
        "read_file" => """{ "path":"sample.cs", "start_line":1.00, "line_count":1e2 }""",
        "read_diff" => """{"path":"sample.cs","start_hunk":1e0,"hunk_count":1.0}""",
        "search_text" => """{"query":"x"}""",
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>Complete synthetic output; all identities, hashes and observations are untrusted shape data.</summary>
    public static string Result(string name, int lineCount = 100)
    {
        var value = new Dictionary<string, object?> { ["status"] = "ok", ["reviewed_identity"] = new
        { repository_id = "synthetic", review_target = 1, base_sha = new string('a', 40), head_sha = new string('b', 40) },
            ["observation_id"] = new string('c', 64) };
        switch (name)
        {
            case "list_files":
                value["prefix"] = null; value["after"] = null; value["paths"] = new[] { "sample.cs" };
                value["truncated"] = false; value["next_after"] = null; break;
            case "list_changed_files":
                value["after"] = null; value["changes"] = new[] { new { path = "sample.cs", previous_path = (string?)null,
                    status = "modified", additions = 1, deletions = 0, changes = 1, patch_status = "available",
                    patch_sha256 = new string('d', 64), source_truncated = false } };
                value["truncated"] = false; value["next_after"] = null; break;
            case "read_file":
                value["path"] = "sample.cs"; value["raw_sha256"] = new string('d', 64); value["requested_start_line"] = 1;
                value["requested_line_count"] = lineCount == 0 ? 100 : lineCount; value["returned_start_line"] = lineCount == 0 ? null : 1;
                if (lineCount == 0) value["status"] = "start_after_eof";
                value["returned_end_line"] = lineCount == 0 ? null : lineCount;
                value["lines"] = Enumerable.Range(1, lineCount).Select(i => new { line = i, text = "x" }).ToArray();
                value["truncated"] = false; value["truncation_reason"] = null; break;
            case "read_diff":
                value["path"] = "sample.cs"; value["patch_sha256"] = new string('d', 64); value["source_truncated"] = false;
                value["requested_start_hunk"] = 1; value["requested_hunk_count"] = 1; value["returned_start_hunk"] = 1; value["returned_end_hunk"] = 1;
                value["hunks"] = new[] { new { old_start = 0, old_count = 0, new_start = 1, new_count = 1,
                    lines = new[] { new { kind = "addition", old_line = (int?)null, new_line = 1, text = "x" } } } };
                value["truncated"] = false; value["next_start_hunk"] = null; break;
            case "search_text":
                value["query_sha256"] = new string('e', 64); value["path"] = null; value["files_scanned"] = 1; value["raw_bytes_scanned"] = 12L;
                foreach (var key in new[] { "skipped_invalid_utf8", "skipped_binary", "skipped_lone_cr", "skipped_oversized" }) value[key] = 0;
                value["matches"] = new[] { new { path = "sample.cs", raw_sha256 = new string('d', 64), line = 1, text = "x" } };
                value["truncated"] = false; value["truncation_reason"] = null; break;
            default: throw new ArgumentOutOfRangeException(nameof(name));
        }
        return JsonSerializer.Serialize(value);
    }

    private static Dictionary<string, object?> Object(Dictionary<string, object?> properties, string[]? required = null) =>
        new() { ["type"] = "object", ["properties"] = properties, ["required"] = required ?? properties.Keys.ToArray(), ["additionalProperties"] = false };
    private static Dictionary<string, object?> String(string? single = null, bool nullable = false) =>
        single is null ? new() { ["type"] = nullable ? new[] { "string", "null" } : (object)"string" } : Strings([single], nullable);
    private static Dictionary<string, object?> Strings(string?[] values, bool nullable = false) =>
        new() { ["type"] = nullable ? new[] { "string", "null" } : (object)"string", ["enum"] = values };
    private static Dictionary<string, object?> Integer(long minimum, long maximum, bool nullable = false) =>
        new() { ["type"] = nullable ? new[] { "integer", "null" } : (object)"integer", ["minimum"] = minimum, ["maximum"] = maximum };
    private static Dictionary<string, object?> Boolean() => new() { ["type"] = "boolean" };
    private static Dictionary<string, object?> Array(object items, int maximum) => new() { ["type"] = "array", ["items"] = items, ["maxItems"] = maximum };
    private static Dictionary<string, object?> IdentitySchema() => Object(new() { ["repository_id"] = String(), ["review_target"] = Integer(1, long.MaxValue),
        ["base_sha"] = String(), ["head_sha"] = String() });
}

/// <summary>A narrow counting capability; it grants neither repository access nor product evidence authority.</summary>
public sealed class StructuredReadCapability : IToolCapability
{
    private readonly Action? entered;
    /// <summary>Creates a counting capability with an optional trusted test observer of actual entries.</summary>
    public StructuredReadCapability(Action? entered = null) => this.entered = entered;
    /// <inheritdoc />
    public string CapabilityId => "synthetic_read";
    /// <summary>Actual entries into the synthetic effect seam.</summary>
    public int Entries { get; private set; }
    /// <summary>The exact numeric value read by the real producer.</summary>
    public long LastCount { get; private set; }
    internal void Enter(long count, CancellationToken token) { token.ThrowIfCancellationRequested(); Entries++; LastCount = count; entered?.Invoke(); }
}

/// <summary>A real independently compiled producer using the complete structured result schema and original raw call.</summary>
public sealed class StructuredReadTool : FunctionTool<StructuredReadCapability>
{
    private readonly bool rejectDomain;
    private readonly string output;
    /// <summary>Creates a bounded synthetic producer, optionally refusing domain admission or returning a malformed candidate.</summary>
    public StructuredReadTool(string name, bool rejectDomain = false, string? result = null)
        : base(new(name, "Synthetic structured read", ToolSchema.Parse(StructuredReadFixtures.InputSchema(name)),
            ToolSchema.Parse(StructuredReadFixtures.ResultSchema(name)), "synthetic_read", ToolEffect.ReadOnly, 2048, 4096))
    { this.rejectDomain = rejectDomain; output = result ?? StructuredReadFixtures.Result(name); }
    /// <inheritdoc />
    protected override bool ValidateArguments(JsonElement arguments) => !rejectDomain && Count(arguments) >= 0;
    /// <inheritdoc />
    protected override ValueTask<ToolOutput> InvokeCoreAsync(ToolCall call, StructuredReadCapability capability, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(call.ArgumentsJson);
        capability.Enter(Count(document.RootElement), cancellationToken);
        return ValueTask.FromResult(ToolOutput.Success(call, output));
    }
    private static long Count(JsonElement root)
    {
        foreach (var key in new[] { "line_count", "hunk_count" })
            if (root.TryGetProperty(key, out var value))
                return ToolInteger.TryParse(value.GetRawText(), out var count) ? count : -1;
        return 0;
    }
}
