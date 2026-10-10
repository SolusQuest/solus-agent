using System.Security.Cryptography;
using System.Text;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Providers;

namespace SolusAgent.ConsumerProbes.CustomProvider;

/// <summary>Independent controlled persistence producer. Its opaque grammar is owned here, not by Runtime or the outer API.</summary>
public sealed class PersistentProvider(bool fail = false) : ModelProvider(new("persistent", "ordinary"), ProviderCapabilities.Continuation | ProviderCapabilities.UsageReporting), IProviderContextPersistence
{
    private readonly string credential = "PERSISTENCE_CREDENTIAL_CANARY";
    public int Effects { get; private set; }
    public int Imports { get; private set; }
    public bool RejectImport { get; set; }
    public bool RejectExport { get; set; }
    public bool UnknownUsage { get; set; }
    public Action<ProviderRequest>? Inspect { get; set; }
    public ProviderRequest? LastRequest { get; private set; }
    protected override ValueTask<ProviderResponse> ExchangeCoreAsync(ProviderRequest request, ProviderObservation observation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (credential.Length == 0) throw new InvalidOperationException();
        Inspect?.Invoke(request); LastRequest = request; Effects++;
        observation.ObserveDispatch(DispatchExposure.Dispatched);
        observation.CaptureUsage(UnknownUsage ? new(null, 2) : new(3, 2), new(UsageSettlement.Settled));
        if (fail) throw new ProviderFailureException(new(ProviderRetryKind.Transient));
        var continuation = new ProviderContinuation(Scope, request.Attempt, Encoding.UTF8.GetBytes("RESTRICTED_REPLAY_CANARY"));
        return ValueTask.FromResult(new ProviderResponse(Scope, request.Attempt, ProviderFinish.Final, "RESTRICTED_FINAL_CANARY", [], continuation));
    }
    public ProviderSavedState? ExportContext(ProviderContextBinding binding) => RejectExport ? null : new(binding.Scope, binding.Origin, 1, Bytes(binding));
    public bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state)
    {
        Imports++;
        return !RejectImport && state.FormatVersion == 1 && Scope.Matches(binding.Scope) && state.Scope.Matches(binding.Scope)
            && state.Origin.Matches(binding.Origin) && state.CopyRestrictedPayload().AsSpan().SequenceEqual(Bytes(binding));
    }
    private static byte[] Bytes(ProviderContextBinding binding)
    {
        // Fully scoped synthetic producer grammar, including exact opaque replay. No credential material.
        var origin = binding.Origin;
        var anchor = Encoding.UTF8.GetBytes($"controlled-v1|{binding.Scope.Provider}|{binding.Scope.Model}|{origin.ExecutionId}|{origin.LogicalCallId}|{origin.PhysicalAttemptId}|{origin.AttemptNumber}");
        return SHA256.HashData([.. anchor, .. binding.Continuation?.CopyReplayBytes() ?? []]);
    }
}
