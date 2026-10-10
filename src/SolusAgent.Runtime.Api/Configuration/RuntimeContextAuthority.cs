using SolusAgent.Api.Context;

namespace SolusAgent.Runtime.Api.Configuration;

/// <summary>Trusted freshly injected Host recovery admission, not saved state or a Runtime-owned registry.</summary>
public interface IRuntimeContextAuthority
{
    /// <summary>Verifies exact envelope metadata/payload against independent trusted evidence and atomically claims the current logical-work source/round and grant.</summary>
    /// <remarks>Must reject stale selections, duplicate/concurrent grants and competing checkpoints. A supplied hash alone is not provenance. Returning true consumes authority even if subsequent local execution fails. Must not dispatch work. Runtime separately validates structural bindings.</remarks>
    bool TryClaim(AgentContextEnvelope context, ContextRoundGrant grant);
}
