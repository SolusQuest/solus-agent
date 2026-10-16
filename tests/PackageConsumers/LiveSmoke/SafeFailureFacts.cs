using System.Diagnostics;
using System.Text.Json;
using SolusAgent.Runtime.Api.Providers;

namespace PackageConsumers.LiveSmoke;

// Retains only explicitly selected observations. No Report, context, exception or wire body is serialized.
internal sealed class SafeFailureFacts
{
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private long? input = 0, output = 0;
    private bool awaitingReport = true;
    private WireObservations wire = new();
    internal string Stage { get; private set; } = "preparation";
    internal int? HttpStatus { get; private set; }
    internal int? ProviderError { get; private set; }
    internal bool? RequestProfileMatch, ResponseProfileMatch, ReplyMatch;
    internal long? HistoricalInput, HistoricalOutput;
    internal bool? ContextRemoved;
    internal TimeSpan Elapsed => elapsed.Elapsed;
    internal void Credential() => Stage = "credential";
    internal void BeginRound(bool fresh)
    {
        wire = new();
        Stage = fresh ? "producer" : "restore"; awaitingReport = true;
        HttpStatus = null; ProviderError = null; RequestProfileMatch = null; ResponseProfileMatch = null; ReplyMatch = null;
        HistoricalInput = null; HistoricalOutput = null;
    }
    internal void BeginDispatch() { HttpStatus = null; ResponseProfileMatch = null; }
    internal void ObserveStatus(int status) => HttpStatus = status is >= 100 and <= 599 ? status : null;
    internal void ObserveReport(Report report)
    {
        wire = (report.Wire ?? new()).Sanitize();
        awaitingReport = false;
        input = Add(input, report.Input); output = Add(output, report.Output);
        HistoricalInput = Number(report.HistoryInput); HistoricalOutput = Number(report.HistoryOutput);
        ProviderError = report.ProviderError is { } error && Enum.IsDefined(error) ? (int)error : null;
        // The child's aggregate starts true; it is not an observed match when the adapter returned no response.
        ReplyMatch = report.ProviderError == SolusAgent.Runtime.Api.Providers.ProviderError.None ? report.ReplyMatches : null;
    }
    internal string Envelope(Failure failure, SmokeHost? host)
    {
        var milliseconds = elapsed.ElapsedMilliseconds;
        var json = JsonSerializer.Serialize(new
        {
            stage = Stage, dispatches = host?.Sends, toolEntries = host?.ToolEntries, toolEffects = host?.Effects,
            elapsedMilliseconds = milliseconds is >= 0 and <= 900000 ? (long?)milliseconds : null,
            httpStatus = HttpStatus, providerError = ProviderError, requestProfileMatch = RequestProfileMatch,
            responseProfileMatch = ResponseProfileMatch, replyMatch = ReplyMatch,
            currentInputTokens = awaitingReport ? null : input, currentOutputTokens = awaitingReport ? null : output,
            historicalInputTokens = HistoricalInput, historicalOutputTokens = HistoricalOutput, contextRemoved = ContextRemoved,
            wireJsonValid = wire.JsonValid, wireUsageValid = wire.UsageValid, wireShape = wire.Shape, wireFinish = wire.Finish,
            wireContent = wire.Content, wireIdentityValid = wire.IdentityValid, wireCallError = wire.CallError, wireArgumentsError = wire.ArgumentsError,
        });
        return $"LiveSmoke failed: {failure}.\nLiveSmoke facts: {json}";
    }
    private static long? Number(long? value) => value is >= 0 and <= 9007199254740991 ? value : null;
    private static long? Add(long? left, long? right) => Number(left) is { } first && Number(right) is { } second
        && first <= 9007199254740991 - second ? first + second : null;
}
