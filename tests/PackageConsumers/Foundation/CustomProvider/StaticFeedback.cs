using SolusAgent.Runtime.Api.Providers;

namespace PackageConsumers.Foundation.CustomProvider;

public static class StaticFeedback
{
    public static void Verify()
    {
        // Independently compiled package consumer: no linked implementation or contract-test fixture.
        string[] keys = ["path_empty", "path_absolute", "path_long", "path_unicode", "path_segment", "path_dot",
            "prefix_empty", "prefix_absolute", "prefix_long", "prefix_unicode", "prefix_segment", "prefix_dot",
            "after_empty", "after_absolute", "after_long", "after_unicode", "after_segment", "after_dot"];
        string[] fields = ["path", "prefix", "after"]; string[] rules = ["empty", "absolute", "long", "unicode", "segment", "dot"];
        var declarations = new List<ToolArgumentError>
        {
            new("indexed", "entry_unavailable", [new("domain", "indexed_entries")]),
            new("modified", "entry_unavailable", [new("domain", "modified_entries")]),
            new("cursor_restart", "cursor_invalid", [new("action", "restart_listing")]),
        };
        declarations.AddRange(keys.Select((key, i) => new ToolArgumentError(key, "entry_path_invalid", [new("field", fields[i / 6]), new("rule", rules[i % 6])])));
        var policy = new ToolArgumentCorrectionPolicy("package_variants", declarations);
        var binding = new ToolArgumentCorrectionBinding("package_classifier", keys, (_, _) => ToolArgumentDisposition.Correct("prefix_empty"));
        if (policy.Errors.Count != 23 || binding.AllowedErrorKeys.Count != 18 || policy.ContainsError("entry_unavailable")
            || policy.GetError("indexed").Json != "{\"status\":\"error\",\"code\":\"entry_unavailable\",\"domain\":\"indexed_entries\",\"retryable\":true}"
            || policy.GetError("modified").Code != "entry_unavailable"
            || policy.GetError("prefix_empty").Json != "{\"status\":\"error\",\"code\":\"entry_path_invalid\",\"field\":\"prefix\",\"rule\":\"empty\",\"retryable\":true}"
            || new ToolArgumentCorrectionPolicy("package_variants", declarations.AsEnumerable().Reverse().ToArray()).SemanticFingerprint != policy.SemanticFingerprint)
            throw new InvalidOperationException("Static package feedback variant contract differed.");
        declarations.Clear(); keys[0] = "changed";
        if (policy.Errors.Count != 23 || !binding.AllowedErrorKeys.Contains("path_empty")) throw new InvalidOperationException("Static inputs were not copied.");
    }
}
