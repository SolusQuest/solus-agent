using System.Text.Json;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;

namespace SolusAgent.Runtime.Context;

internal static partial class OrdinaryContextCodec
{
    private static ProviderAttempt? ModelAttempt(SavedInput entry) => entry.Final?.Attempt ?? entry.ToolModel?.Attempt ?? entry.CorrectionModel?.Attempt;
    private static ProviderScope? ModelScope(SavedInput entry) => entry.Final?.Scope ?? entry.ToolModel?.Scope ?? entry.CorrectionModel?.Scope;
    private static ProviderExchangeBounds? ModelBounds(SavedInput entry) => entry.Final?.Bounds ?? entry.ToolModel?.Bounds ?? entry.CorrectionModel?.Bounds;
    private static SavedContinuation? ModelContinuation(SavedInput entry) => entry.Final?.Continuation ?? entry.ToolModel?.Continuation ?? entry.CorrectionModel?.Continuation;

    private static ProviderResponse RestoreCorrection(SavedCorrectionModel model, ProviderRequest original)
    {
        if (!model.Scope.Matches(original.Scope) || !model.Attempt.Matches(original.Attempt)
            || model.Members.Length == 0 || model.Members.Where((member, i) => member.Ordinal != i + 1).Any()) throw new JsonException();
        return ProviderResponse.RestoreToolCorrection(original, model.Text, model.Continuation?.Restore(), model.Origin,
            model.Members.Select(m => new ProviderCorrectionMember(m.Call, m.OriginalArgumentBytes, m.ErrorCode, m.SchemaError)).ToArray(),
            model.OriginalResponseBytes, model.DefinitionFingerprint);
    }

    // Pure grammar validation only; no provider callback, tool, claim, import or dispatch occurs.
    // Full original request association is reconstructed again after Host claim in Materialize.
    private static ProviderResponse CorrectionCandidate(SavedCorrectionModel model, RuntimeConfiguration config, RuntimeOptions options)
    {
        var required = options.RequireContinuation || model.Continuation is not null ? ProviderCapabilities.Continuation : ProviderCapabilities.None;
        var original = new ProviderRequest(config.Scope, model.Attempt, [], config.Tools.Select(t => t.Descriptor).ToArray(),
            requiredCapabilities: required, bounds: model.Bounds, toolArgumentCorrection: config.ToolArgumentCorrection);
        return RestoreCorrection(model, original);
    }

    private static int CorrectionFeedbackBytes(SavedCorrectionFeedback feedback, OrdinaryCheckpoint saved, RuntimeConfiguration config, RuntimeOptions options)
    {
        var source = saved.Records.Single(r => r.CorrectionModel?.Attempt.Matches(feedback.ModelAttempt) == true).CorrectionModel!;
        return ProviderInput.FromCorrectionFeedback(CorrectionCandidate(source, config, options), feedback.Ordinal - 1).PayloadByteCount;
    }

    private static void ValidateAdmissionCount(SavedRound round, OrdinaryCheckpoint saved)
    {
        var normal = saved.Members.Count(m => m.ModelAttempt.ExecutionId == round.Usage.ExecutionId);
        var corrections = saved.Records.Where(r => r.CorrectionModel?.Attempt.ExecutionId == round.Usage.ExecutionId)
            .Sum(r => r.CorrectionModel!.Members.Length);
        if (round.Usage.ToolCallAdmissions?.Admitted != checked(normal + corrections)) throw new JsonException();
    }
}
