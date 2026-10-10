using SolusAgent.Tools.Api;

namespace CustomTools;

/// <summary>An independently compiled Tools.Api consumer of trusted historical facts, with no runtime reference.</summary>
public static class HistoricalToolResults
{
    /// <summary>Restores original facts without accessing a tool or capability.</summary>
    public static ToolResult Restore(ToolDescriptor descriptor, ToolCall original, ToolResult recorded) =>
        ToolResult.RestoreHistorical(descriptor, original,
            new(recorded.Call.CallId, recorded.Call.ToolName, recorded.Call.ArgumentsJson),
            recorded.Outcome, recorded.Error, recorded.InvocationStarted, recorded.Json);
}
