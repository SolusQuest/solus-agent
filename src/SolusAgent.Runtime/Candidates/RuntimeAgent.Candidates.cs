using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.Runtime.Candidates;

namespace SolusAgent.Runtime.Execution;

internal sealed partial class RuntimeAgent
{
    public ValueTask<CandidateExecutionResult> ExecuteCandidatesAsync(CandidateExecutionRequest request, ICandidateHost host,
        IProgress<AgentProgress>? progress = null, CancellationToken cancellationToken = default) =>
        CandidateExecutionDriver.ExecuteAsync(configuration, options, request, host, progress, cancellationToken);
}
