using System.Text.Json;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;

namespace SolusAgent.Runtime.Context;

internal static partial class OrdinaryContextCodec
{
    internal static int CandidateBytes(SavedCandidates value) => JsonSerializer.SerializeToUtf8Bytes(value, Json).Length;

    private static void ValidateCandidates(OrdinaryCheckpoint saved, RuntimeOptions options)
    {
        var ledger = saved.Candidates!;
        if (!Enum.IsDefined(ledger.Cursor) || ledger.Episodes.Length is < 1 or > 64
            || ledger.Submissions.Length > 64 || saved.Rounds.Any(r => r.Candidates is null)
            || ledger.Episodes.Select(e => e.Id).Distinct().Count() != ledger.Episodes.Length
            || ledger.Submissions.Select(s => s.SubmissionId).Distinct().Count() != ledger.Submissions.Length
            || saved.Records.Length + ledger.Episodes.Length + ledger.Submissions.Length > options.MaximumRecords) throw new JsonException();
        var facts = saved.Rounds.SelectMany(r => r.Facts).ToArray();
        var roundIndex = saved.Rounds.Select((r, i) => (r.RoundId, i)).ToDictionary(x => x.RoundId, x => x.i);
        var episodes = ledger.Episodes.ToDictionary(e => e.Id);
        var submissions = ledger.Submissions.ToDictionary(s => s.SubmissionId);
        var finalIndices = saved.Records.Select((r, i) => (r, i)).Where(x => x.r.Final is not null).Select(x => x.i).ToArray();
        if (!finalIndices.SequenceEqual(ledger.Submissions.Select(s => s.FinalIndex))) throw new JsonException();
        var firstWork = saved.Rounds[0].LogicalWorkId;
        if (ledger.Episodes[0].WorkId != firstWork) throw new JsonException();
        var priorFirst = -1; var correctionIndices = new HashSet<int>();
        for (var index = 0; index < ledger.Episodes.Length; index++)
        {
            var episode = ledger.Episodes[index];
            if (episode.Id == Guid.Empty || episode.WorkId == Guid.Empty || (episode.FirstAttempt is null) != (episode.AdmissionRound is null)) throw new JsonException();
            SavedCandidateSubmission? predecessor = null;
            if (episode.Predecessor is { } id)
            {
                if (!submissions.TryGetValue(id, out predecessor) || predecessor.Receipt is not { Acknowledgement: CandidateAcknowledgement.Acknowledged, Continuation: CandidateContinuation.Continue }
                    || index == 0 || predecessor.EpisodeId != ledger.Episodes[index - 1].Id
                    || episode.WorkId != ledger.Episodes[index - 1].WorkId) throw new JsonException();
            }
            else if (index != 0 && (episode.WorkId == ledger.Episodes[index - 1].WorkId
                || ledger.Submissions.SingleOrDefault(s => s.EpisodeId == ledger.Episodes[index - 1].Id)?.Receipt is not
                    { Acknowledgement: CandidateAcknowledgement.Acknowledged, Continuation: CandidateContinuation.End })) throw new JsonException();
            var needsCorrection = predecessor?.Receipt?.Decision == CandidateDecision.Reject && predecessor.Correction is not null;
            if (episode.CorrectionIndex is { } correction)
            {
                if (!needsCorrection || correction <= predecessor!.FinalIndex || correction >= saved.Records.Length || !correctionIndices.Add(correction)
                    || saved.Records[correction] is not { Kind: ProviderInputKind.InputData } data || data.Text != predecessor.Correction) throw new JsonException();
            }
            if (episode.FirstAttempt is { } first)
            {
                var firstIndex = Array.FindIndex(facts, f => f.Attempt.Matches(first));
                if (firstIndex <= priorFirst || !roundIndex.TryGetValue(episode.AdmissionRound!.Value, out var admitted)
                    || saved.Rounds[admitted].LogicalWorkId != episode.WorkId
                    || !saved.Rounds[admitted].Facts.Any(f => f.Attempt.Matches(first))
                    || needsCorrection && episode.CorrectionIndex is null
                    || predecessor?.InvocationRound is { } invoked && roundIndex[invoked] > admitted) throw new JsonException();
                priorFirst = firstIndex;
            }
            else if (index != ledger.Episodes.Length - 1) throw new JsonException();
        }
        for (var index = 0; index < ledger.Submissions.Length; index++)
        {
            var submission = ledger.Submissions[index];
            if (submission.ExecutionId == Guid.Empty || submission.SubmissionId == Guid.Empty || !episodes.TryGetValue(submission.EpisodeId, out var episode)
                || episode.FirstAttempt is null || submission.FinalIndex < 0 || submission.FinalIndex >= saved.Records.Length
                || saved.Records[submission.FinalIndex].Final is not { } final || final.Attempt.ExecutionId != submission.ExecutionId
                || (submission.Invoked ? submission.Receipt is null || submission.InvocationRound is null : submission.Receipt is not null || submission.InvocationRound is not null)
                || submission.Receipt is { } receipt && (receipt.ExecutionId != submission.ExecutionId || receipt.SubmissionId != submission.SubmissionId)
                || submission.InvocationRound is { } invocation && (!roundIndex.TryGetValue(invocation, out var invoked)
                    || invoked < roundIndex[episode.AdmissionRound!.Value])
                || !submission.Invoked && index != ledger.Submissions.Length - 1) throw new JsonException();
            var firstIndex = Array.FindIndex(facts, f => f.Attempt.Matches(episode.FirstAttempt));
            var finalIndex = Array.FindIndex(facts, f => f.Attempt.Matches(final.Attempt));
            var nextEpisode = Array.FindIndex(ledger.Episodes, e => e.Id == episode.Id) + 1;
            if (firstIndex < 0 || finalIndex < firstIndex || nextEpisode < ledger.Episodes.Length && ledger.Episodes[nextEpisode].FirstAttempt is { } next
                && finalIndex >= Array.FindIndex(facts, f => f.Attempt.Matches(next))) throw new JsonException();
            if (submission.Correction is not null)
            {
                if (submission.Receipt is not { Acknowledgement: CandidateAcknowledgement.Acknowledged } known) throw new JsonException();
                _ = new CandidateFeedback(submission.ExecutionId, submission.SubmissionId, known.Acknowledgement, known.Decision, known.Continuation, submission.Correction);
            }
        }
        // Every model operation belongs to one admitted episode. An accepted Final closes that episode's production.
        var admittedEpisodes = ledger.Episodes.Where(e => e.FirstAttempt is not null).ToArray();
        for (var i = 0; i < admittedEpisodes.Length; i++)
        {
            var episode = admittedEpisodes[i];
            var start = Array.FindIndex(facts, f => f.Attempt.Matches(episode.FirstAttempt!));
            var end = i + 1 < admittedEpisodes.Length ? Array.FindIndex(facts, f => f.Attempt.Matches(admittedEpisodes[i + 1].FirstAttempt!)) : facts.Length;
            var segment = facts[start..end];
            if (segment.Any(f => saved.Rounds.Single(r => r.Facts.Contains(f)).LogicalWorkId != episode.WorkId)
                || segment.Length == 0 || segment.Take(segment.Length - 1).Any(f => f.AcceptedFinal)
                || ledger.Submissions.Count(s => s.EpisodeId == episode.Id) > 1) throw new JsonException();
            for (var j = 1; j < segment.Length; j++)
                if (segment[j].Attempt.LogicalCallId != segment[j - 1].Attempt.LogicalCallId && !segment[j - 1].AcceptedTools
                    || segment[j].Attempt.LogicalCallId == segment[j - 1].Attempt.LogicalCallId
                        && (segment[j - 1].Retry is null || !segment[j - 1].ClosureAcknowledged
                            || segment[j].Attempt.AttemptNumber != segment[j - 1].Attempt.AttemptNumber + 1)) throw new JsonException();
            if (episode.CorrectionIndex is { } correctionIndex && saved.Records.Select((r, n) => (r, n))
                .Any(x => (x.r.Final?.Attempt ?? x.r.ToolModel?.Attempt) is { } model
                    && segment.Any(f => f.Attempt.Matches(model)) && x.n <= correctionIndex)) throw new JsonException();
            if (episode.Predecessor is { } predecessorId)
            {
                var predecessor = submissions[predecessorId];
                if (episode.CorrectionIndex is { } correction && saved.Records.Take(correction).Count(r => r.Final is not null) !=
                    ledger.Submissions.TakeWhile(s => s.SubmissionId != predecessorId).Count() + 1) throw new JsonException();
            }
        }
        // After model history starts, data is either a marked once-only correction or new-run Host input.
        var firstModel = Array.FindIndex(saved.Records, r => r.Final is not null || r.ToolModel is not null);
        for (var recordIndex = Math.Max(0, firstModel + 1); firstModel >= 0 && recordIndex < saved.Records.Length; recordIndex++)
        {
            if (saved.Records[recordIndex].Kind != ProviderInputKind.InputData || correctionIndices.Contains(recordIndex)) continue;
            var newRunData = ledger.Episodes.Select((e, i) => (e, i)).Where(x => x.i > 0 && x.e.Predecessor is null).Any(x =>
            {
                var prior = ledger.Submissions.Single(s => s.EpisodeId == ledger.Episodes[x.i - 1].Id).FinalIndex;
                var firstModelInRun = Array.FindIndex(saved.Records, r => (r.Final?.Attempt ?? r.ToolModel?.Attempt) is { } origin
                    && saved.Rounds.Where(round => round.LogicalWorkId == x.e.WorkId).SelectMany(round => round.Facts).Any(f => f.Attempt.Matches(origin)));
                return recordIndex > prior && (firstModelInRun < 0 || recordIndex < firstModelInRun);
            });
            if (!newRunData) throw new JsonException();
        }
        if (facts.Length != 0 && (admittedEpisodes.Length == 0 || !facts[0].Attempt.Matches(admittedEpisodes[0].FirstAttempt!))) throw new JsonException();
        var lastEpisode = ledger.Episodes[^1]; var lastSubmission = ledger.Submissions.LastOrDefault();
        var expected = lastSubmission is not null && lastSubmission.EpisodeId == lastEpisode.Id
            ? !lastSubmission.Invoked ? CandidateCursor.FinalReadyUnsubmitted
                : lastSubmission.Receipt!.Acknowledgement != CandidateAcknowledgement.Acknowledged ? CandidateCursor.UnresolvedFeedback
                : lastSubmission.Receipt.Continuation == CandidateContinuation.End ? CandidateCursor.End : (CandidateCursor?)null
            : lastEpisode.FirstAttempt is not null ? CandidateCursor.ProducingAdmittedEpisode
                : lastEpisode.Predecessor is not null ? CandidateCursor.FollowOnNotAdmitted : CandidateCursor.Initial;
        if (expected is null || ledger.Cursor != expected || lastEpisode.WorkId != saved.Info.LogicalWorkId) throw new JsonException();
        foreach (var round in saved.Rounds)
        {
            var detail = round.Candidates!;
            var observed = ledger.Submissions.Where(s => s.InvocationRound is { } invoked && roundIndex[invoked] <= roundIndex[round.RoundId])
                .Select(s => s.Receipt!).ToArray();
            var admissions = ledger.Episodes.Where(e => e.AdmissionRound == round.RoundId && e.Predecessor is not null)
                .Select(e => submissions[e.Predecessor!.Value].Receipt!).ToArray();
            if (detail.Submissions != ledger.Submissions.Count(s => s.InvocationRound == round.RoundId)
                || detail.Repairs != admissions.Count(r => r.Decision == CandidateDecision.Reject)
                || detail.Continuations != admissions.Count(r => r.Decision == CandidateDecision.Accept)) throw new JsonException();
            if (detail.Stop == CandidateStopReason.Completed && observed.LastOrDefault() is not
                    { Acknowledgement: CandidateAcknowledgement.Acknowledged, Decision: CandidateDecision.Accept, Continuation: CandidateContinuation.End }
                || detail.Stop == CandidateStopReason.HostEnded && observed.LastOrDefault()?.Decision != CandidateDecision.Reject) throw new JsonException();
            _ = new CandidateExecutionResult(new(round.Usage.ExecutionId, round.Reason, round.Completed,
                failureCode: round.Reason == AgentTerminationReason.Failed
                    ? detail.Stop == CandidateStopReason.ProgressObserverFailed ? AgentFailureCode.ProgressObserverFailed : AgentFailureCode.ExecutionFailed : AgentFailureCode.None,
                usage: round.Usage), detail.Stop, observed.Where(r => r.ExecutionId == round.Usage.ExecutionId).ToArray(), detail.Repairs, detail.Continuations,
                observed.Where(r => r.ExecutionId != round.Usage.ExecutionId).ToArray());
        }
    }
}
