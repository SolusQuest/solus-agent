using SolusAgent.Api.Candidates;
using SolusAgent.Runtime.Api.Providers;

namespace SolusAgent.Runtime.Context;

internal enum CandidateCursor { Initial, FinalReadyUnsubmitted, UnresolvedFeedback, FollowOnNotAdmitted, ProducingAdmittedEpisode, End }
internal sealed record SavedCandidateEpisode(Guid Id, Guid WorkId, Guid? Predecessor, int? CorrectionIndex,
    ProviderAttempt? FirstAttempt, Guid? AdmissionRound);
internal sealed record SavedCandidateSubmission(Guid EpisodeId, Guid ExecutionId, Guid SubmissionId, int FinalIndex,
    bool Invoked, Guid? InvocationRound, CandidateReceipt? Receipt, string? Correction);
internal sealed record SavedCandidates(CandidateCursor Cursor, SavedCandidateEpisode[] Episodes, SavedCandidateSubmission[] Submissions);
internal sealed record SavedCandidateRound(CandidateStopReason Stop, int Submissions, int Repairs, int Continuations);
