using System.Text.Json;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;

namespace PackageConsumers.Apr.Business;

// Product policy and progress live in the independently compiled Api-only Host.
public sealed class AprBusinessHost(string directory, bool unknownFeedback = false) : ICandidateHost
{
    public const string Instruction = "Review only the selected synthetic snapshot.";
    public const string Data = "APR_UNTRUSTED_SNAPSHOT_CANARY";
    public const string AcceptedPayload = "{\"item\":\"A\",\"value\":1,\"evidence\":\"APR_ACCEPTED_PAYLOAD_CANARY\"}";
    public const string RejectedPayload = "{\"item\":\"B\",\"value\":-1,\"evidence\":\"APR_REJECTED_PAYLOAD_CANARY\"}";
    public const string RepairedPayload = "{\"item\":\"B\",\"value\":2,\"evidence\":\"APR_REPAIRED_PAYLOAD_CANARY\"}";
    public const string Correction = "APR_CORRECTION_CANARY: B requires value 2.";
    public int Calls { get; private set; }
    public int NewEffects { get; private set; }
    public IReadOnlyList<Acceptance> Accepted => Directory.GetFiles(directory, "accepted-*.json")
        .Select(path => Store.Read<Acceptance>(path)).OrderBy(row => row.Item, StringComparer.Ordinal).ToArray();
    public int Effects => Directory.GetFiles(directory, "effect-*.json").Length;

    public ValueTask<ContextCandidateExecutionResult> ExecuteAsync(IContextCandidateAgent agent, ContextExecutionRequest request,
        CandidateExecutionBounds bounds, IRestrictedContextSink sink, IProgress<AgentProgress> progress) =>
        agent.ExecuteCandidatesWithContextAsync(request, bounds, this, sink, progress);

    public ValueTask<CandidateFeedback?> SubmitAsync(CandidateSubmission submission, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Calls++;
        using var document = JsonDocument.Parse(submission.Payload);
        var item = document.RootElement.GetProperty("item").GetString();
        var value = document.RootElement.GetProperty("value").GetInt32();
        if (item == "B" && value != 2)
        {
            Store.Write(Path.Combine(directory, "rejected.json"), new Rejection(submission.ExecutionId, submission.SubmissionId));
            return ValueTask.FromResult<CandidateFeedback?>(new(submission.ExecutionId, submission.SubmissionId,
                CandidateAcknowledgement.Acknowledged, CandidateDecision.Reject, CandidateContinuation.Continue, Correction));
        }
        if (item is not ("A" or "B") || value != (item == "A" ? 1 : 2)) throw new InvalidOperationException("Invalid synthetic candidate.");
        if (unknownFeedback && item == "B")
            return ValueTask.FromResult<CandidateFeedback?>(new(submission.ExecutionId, submission.SubmissionId, CandidateAcknowledgement.Unknown));
        if (Accepted.Any(row => row.Item == item)) throw new InvalidOperationException("Accepted progress was resubmitted.");
        if (item == "B" && File.Exists(Path.Combine(directory, "rejected.json"))
            && submission.RepairsSubmissionId != Store.Read<Rejection>(Path.Combine(directory, "rejected.json")).SubmissionId)
            throw new InvalidOperationException("Repair lost its original submission association.");
        var accepted = new Acceptance(submission.ExecutionId, submission.SubmissionId, submission.RepairsSubmissionId, item, value);
        Store.WriteOnce(Path.Combine(directory, $"accepted-{submission.SubmissionId}.json"), accepted);
        ApplyAcceptedEffect(accepted);
        return ValueTask.FromResult<CandidateFeedback?>(new(submission.ExecutionId, submission.SubmissionId,
            CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept, item == "A" ? CandidateContinuation.Continue : CandidateContinuation.End));
    }

    // Acceptance and effects are distinct Host operations, never inferred from Runtime completion.
    private void ApplyAcceptedEffect(Acceptance accepted)
    {
        if (!Accepted.Contains(accepted)) throw new InvalidOperationException("Effect needs independent acceptance.");
        Store.WriteOnce(Path.Combine(directory, $"effect-{accepted.SubmissionId}.json"), accepted.SubmissionId);
        NewEffects++;
    }

    public void ObserveCampaign(AgentRunUsage usage)
    {
        foreach (var attempt in usage.Attempts)
        {
            var row = new CampaignAttempt(attempt.ExecutionId, attempt.LogicalCallId, attempt.PhysicalAttemptId, attempt.AttemptNumber,
                attempt.Usage.InputTokens, attempt.Usage.OutputTokens,
                attempt.Exposure == DispatchExposure.NotDispatched ? 0 : attempt.Usage.InputTokens ?? 64,
                attempt.Exposure == DispatchExposure.NotDispatched ? 0 : attempt.Usage.OutputTokens ?? 16);
            var path = Path.Combine(directory, $"usage-{attempt.PhysicalAttemptId}.json");
            if (File.Exists(path))
            {
                if (Store.Read<CampaignAttempt>(path) != row) throw new InvalidOperationException("Historical usage changed.");
            }
            else Store.WriteOnce(path, row);
        }
    }
    public IReadOnlyList<CampaignAttempt> Campaign => Directory.GetFiles(directory, "usage-*.json").Select(Store.Read<CampaignAttempt>).ToArray();
}

public sealed record Acceptance(Guid ExecutionId, Guid SubmissionId, Guid? RepairsSubmissionId, string Item, int Value);
public sealed record Rejection(Guid ExecutionId, Guid SubmissionId);
public sealed record CampaignAttempt(Guid ExecutionId, Guid LogicalCallId, Guid PhysicalAttemptId, int Ordinal,
    long? Input, long? Output, long InputCharge, long OutputCharge);

public static class Store
{
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path)) ?? throw new InvalidOperationException("Missing fixture state.");
    public static void Write<T>(string path, T value) => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value));
    public static void WriteOnce<T>(string path, T value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(JsonSerializer.SerializeToUtf8Bytes(value)); stream.Flush(true);
    }
}

public sealed class RestrictedSink : IRestrictedContextSink
{
    public AgentContextEnvelope? Envelope { get; private set; }
    public int Calls { get; private set; }
    public void Capture(AgentContextEnvelope context)
    {
        Calls++;
        Envelope = new(context.ImplementationId, context.FormatVersion, context.CompatibilityVersion, context.CopyRestrictedPayload());
    }
}
