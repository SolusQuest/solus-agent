using System.Text.Json;

namespace PackageConsumers.LiveSmoke;

internal static class SafeSummary
{
    internal static string Create(Report first, Report second, int sends, int tools, int effects, TimeSpan elapsed, bool removed)
    {
        Check.That(double.IsFinite(elapsed.TotalMilliseconds) && elapsed >= TimeSpan.Zero && elapsed <= TimeSpan.FromMinutes(5), Failure.Capacity);
        return JsonSerializer.Serialize(new
        {
            dispatches = sends, toolEntries = tools, toolEffects = effects, distinctProcesses = first.Pid != second.Pid,
            producer = new { inputTokens = first.Input, outputTokens = first.Output, attempts = first.RuntimeAttempts, tools = first.RuntimeTools },
            restorer = new { inputTokens = second.Input, outputTokens = second.Output, attempts = second.RuntimeAttempts, tools = second.RuntimeTools,
                historicalInputTokens = second.HistoryInput, historicalOutputTokens = second.HistoryOutput,
                historicalAttempts = second.HistoryAttempts, historicalTools = second.HistoryTools },
            currentInputTokens = Add(first.Input, second.Input), currentOutputTokens = Add(first.Output, second.Output),
            elapsedMilliseconds = Math.Round(elapsed.TotalMilliseconds, 3), contextRemoved = removed,
        });
    }
    private static long? Add(long? left, long? right) => left.HasValue && right.HasValue ? checked(left.Value + right.Value) : null;
}
