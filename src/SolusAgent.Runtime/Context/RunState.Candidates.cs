using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;

namespace SolusAgent.Runtime.Execution;

internal sealed partial class RunState
{
    private int candidateMetadataBytes;
    internal Guid CandidateWorkId => logicalWork;
    internal Guid CandidateRoundId => contextRound;
    internal int CandidateRecordCount => records.Count;

    // A restored checkpoint already charges its complete old ledger. Only positive growth is new retention.
    internal void InitializeCandidateRetention(Context.SavedCandidates value) =>
        candidateMetadataBytes = restored is null ? 0 : Context.OrdinaryContextCodec.CandidateBytes(value);

    internal bool ReserveCandidateLedger(Context.SavedCandidates value, int additionalRecords = 0, int additionalBytes = 0)
    {
        var size = Context.OrdinaryContextCodec.CandidateBytes(value);
        var delta = Math.Max(0, size - candidateMetadataBytes) + additionalBytes;
        if (records.Count + value.Episodes.Length + value.Submissions.Length + additionalRecords > Options.MaximumRecords
            || delta > Options.MaximumRetainedBytes - retainedBytes)
        { Close(RuntimeStop.ResourceLimit); return false; }
        retainedBytes += delta;
        candidateMetadataBytes = Math.Max(candidateMetadataBytes, size);
        return true;
    }
}
