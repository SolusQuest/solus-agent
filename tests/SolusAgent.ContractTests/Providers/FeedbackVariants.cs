using SolusAgent.Runtime.Api.Providers;

namespace SolusAgent.ContractTests.Providers;

internal static class FeedbackVariants
{
    internal static readonly string[] Fields = ["path", "prefix", "after"];
    internal static readonly string[] Rules = ["empty", "absolute", "too_long", "invalid_unicode", "empty_segment", "dot_segment"];
    // This closed startup inventory is independent of rejected call content.
    internal static readonly string[] FieldKeys = [
        "path_empty", "path_absolute", "path_too_long", "path_invalid_unicode", "path_empty_segment", "path_dot_segment",
        "prefix_empty", "prefix_absolute", "prefix_too_long", "prefix_invalid_unicode", "prefix_empty_segment", "prefix_dot_segment",
        "after_empty", "after_absolute", "after_too_long", "after_invalid_unicode", "after_empty_segment", "after_dot_segment"];
    internal static ToolArgumentError[] Errors(bool changed = false) => [
        new("indexed_scope", "entry_unavailable", [new("domain", changed ? "other_entries" : "indexed_entries")]),
        new("modified_scope", "entry_unavailable", [new("domain", "modified_entries")]),
        new("cursor_restart", "cursor_invalid", [new("action", "restart_listing")]),
        .. FieldKeys.Select((key, i) => new ToolArgumentError(key, "entry_path_invalid", [new("field", Fields[i / 6]), new("rule", Rules[i % 6])]))];
    internal static ToolArgumentCorrectionPolicy Policy(bool changed = false) => new("variant_policy", Errors(changed));
    internal static string Expected(string key) => key switch
    {
        "indexed_scope" => "{\"status\":\"error\",\"code\":\"entry_unavailable\",\"domain\":\"indexed_entries\",\"retryable\":true}",
        "modified_scope" => "{\"status\":\"error\",\"code\":\"entry_unavailable\",\"domain\":\"modified_entries\",\"retryable\":true}",
        "cursor_restart" => "{\"status\":\"error\",\"code\":\"cursor_invalid\",\"action\":\"restart_listing\",\"retryable\":true}",
        ToolArgumentCorrectionPolicy.BatchNotExecuted => "{\"status\":\"error\",\"code\":\"batch_not_executed\",\"retryable\":true}",
        ToolArgumentCorrectionPolicy.ArgumentsInvalid => "{\"status\":\"error\",\"code\":\"arguments_invalid\",\"retryable\":true}",
        _ => FieldJson(Array.IndexOf(FieldKeys, key)),
    };
    private static string FieldJson(int index) => "{\"status\":\"error\",\"code\":\"entry_path_invalid\",\"field\":\"" + Fields[index / 6]
        + "\",\"rule\":\"" + Rules[index % 6] + "\",\"retryable\":true}";
}
