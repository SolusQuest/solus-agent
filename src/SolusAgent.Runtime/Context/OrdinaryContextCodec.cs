using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;

namespace SolusAgent.Runtime.Context;

internal static class OrdinaryContextCodec
{
    internal static readonly Guid Implementation = new("3f520f02-3b59-431e-8acf-a62544f28760");
    internal const int MaximumEncodedBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = CreateJson();
    private static JsonSerializerOptions CreateJson()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(t =>
        {
            if (t.Kind != JsonTypeInfoKind.Object) return;
            var ctor = t.Type.GetConstructors().SingleOrDefault();
            if (ctor is null) return;
            var names = ctor.GetParameters().Select(p => p.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var p in t.Properties.ToArray()) if (!names.Contains(p.Name)) t.Properties.Remove(p);
        });
        return new() { TypeInfoResolver = resolver, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, MaxDepth = 32 };
    }
    internal static AgentContextEnvelope Encode(OrdinaryCheckpoint value, RuntimeOptions options)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > Math.Min(MaximumEncodedBytes, options.MaximumRetainedBytes)) throw new JsonException();
        return new(Implementation, 1, 1, bytes);
    }
    // Exact constructor-backed grammar, including optional-but-serialized fields, precedes domain constructors.
    private static void Shape(JsonElement element, Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (element.ValueKind == JsonValueKind.Null) return;
        if (element.ValueKind == JsonValueKind.Array)
        {
            var item = type.IsArray ? type.GetElementType()! : type.GetGenericArguments().Single();
            if (element.GetArrayLength() > 64) throw new JsonException();
            foreach (var child in element.EnumerateArray()) { if (child.ValueKind == JsonValueKind.Null) throw new JsonException(); Shape(child, item); }
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;
        var contract = Json.GetTypeInfo(type);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in element.EnumerateObject())
        {
            var spec = contract.Properties.SingleOrDefault(s => s.Name == p.Name) ?? throw new JsonException();
            if (!seen.Add(p.Name)) throw new JsonException();
            if (p.Value.ValueKind == JsonValueKind.Null && Nullable.GetUnderlyingType(spec.PropertyType) is null
                && new NullabilityInfoContext().Create(type.GetProperty(spec.Name)!).ReadState != NullabilityState.Nullable) throw new JsonException();
            Shape(p.Value, spec.PropertyType);
        }
        if (seen.Count != contract.Properties.Count) throw new JsonException();
    }
    internal static string Binding(RuntimeConfiguration config, RuntimeOptions options) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { config.Scope, Tools = config.Tools.Select(t => t.Descriptor).ToArray(), config.Bounds,
            config.RequiredAcknowledgement, config.RequiredGuarantees, options.RequireContinuation })));

    internal static RestoredOrdinary Decode(ContextExecutionRequest request, RuntimeConfiguration config, RuntimeOptions options)
    {
        var envelope = request.Context!;
        if (envelope.ImplementationId != Implementation) throw new ContextRejected(ContextRejectionCode.ImplementationMismatch);
        if (envelope.FormatVersion != 1 || envelope.CompatibilityVersion != 1) throw new ContextRejected(ContextRejectionCode.UnsupportedFormat);
        if (envelope.PayloadByteCount > Math.Min(MaximumEncodedBytes, options.MaximumRetainedBytes)) throw new JsonException();
        var bytes = envelope.CopyRestrictedPayload();
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
        Shape(document.RootElement, typeof(OrdinaryCheckpoint));
        var saved = JsonSerializer.Deserialize<OrdinaryCheckpoint>(bytes, Json)!;
        Validate(saved, config, options);
        var grant = request.RoundGrant;
        if (grant is null || grant.Source != saved.Info || grant.ExecutionId != request.Request.ExecutionId
            || saved.Rounds.Any(r => r.RoundId == grant.RoundId || r.Usage.ExecutionId == grant.ExecutionId))
            throw new ContextRejected(ContextRejectionCode.InvalidRunTransition);
        var continuing = request.Intent == ContextExecutionIntent.ContinueRun;
        if (continuing ? !saved.RetryEligible : !saved.Final) throw new ContextRejected(ContextRejectionCode.InvalidRunTransition);
        if (continuing && (request.Request.Data.Count != 0 || request.Request.Instructions != saved.Records[0].Text
            || request.Request.UsageLimits?.RetryPolicy is null)) throw new ContextRejected(ContextRejectionCode.InvalidRunTransition);
        var records = Rehydrate(saved, config, options);
        var continuation = records.LastOrDefault(r => r.Model is not null)?.Model?.Continuation;
        var last = saved.Rounds[^1].Facts.LastOrDefault();
        if (saved.Provider is { } provider)
        {
            if (config.Provider is not IProviderContextPersistence persistence || last is null
                || !provider.Scope.Matches(config.Scope) || !provider.Origin.Matches(last.Attempt)
                || !persistence.AdmitContext(new(config.Scope, last.Attempt, continuation),
                    new(provider.Scope, provider.Origin, provider.FormatVersion, provider.Bytes)))
                throw new ContextRejected(ContextRejectionCode.IncompatibleContext);
        }
        else if (last is not null) throw new ContextRejected(ContextRejectionCode.UnsupportedContext);
        if (!continuing)
        {
            records[0] = ProviderInput.Instruction(request.Request.Instructions);
            foreach (var data in request.Request.Data) records.Add(ProviderInput.Data(data.Text));
        }
        if (records.Count >= options.MaximumRecords || records.Count >= config.Bounds.MaximumInputs) throw new JsonException();
        var pending = continuing ? last!.Attempt : null;
        // Validate the exact next request before consuming the Host grant. No provider or exposure is invoked.
        var next = new ProviderAttempt(request.Request.ExecutionId, pending?.LogicalCallId ?? Guid.NewGuid(), Guid.NewGuid(),
            pending is null ? 1 : checked(pending.AttemptNumber + 1));
        var origins = records.Where(r => r.Model is not null).Select(r => r.Model!.Attempt).ToArray();
        var nextRequest = new ProviderRequest(config.Scope, next, records, config.Tools.Select(t => t.Descriptor).ToArray(), continuation,
            options.RequireContinuation ? ProviderCapabilities.Continuation : ProviderCapabilities.None,
            continuing ? saved.OriginalBounds : config.Bounds, new(origins, pending));
        var retained = checked(bytes.Length + (continuing ? 0 : System.Text.Encoding.UTF8.GetByteCount(request.Request.Instructions)
            + request.Request.Data.Sum(d => System.Text.Encoding.UTF8.GetByteCount(d.Text))));
        if (saved.Rounds.Sum(r => r.Usage.Attempts.Count) >= options.MaximumAttempts
            || retained >= options.MaximumRetainedBytes
            || continuing && nextRequest.Bounds.MaximumResponseBytes > options.MaximumRetainedBytes - retained) throw new JsonException();
        return new(saved, records, continuation, pending, continuing ? saved.Info.LogicalWorkId : Guid.NewGuid(), grant.RoundId, retained);
    }
    private static List<ProviderInput> Rehydrate(OrdinaryCheckpoint saved, RuntimeConfiguration config, RuntimeOptions options)
    {
        var records = new List<ProviderInput>();
        ProviderContinuation? continuation = null;
        foreach (var entry in saved.Records)
        {
            if (entry.Kind is ProviderInputKind.HostInstruction or ProviderInputKind.InputData)
            {
                if (entry.Text is null || entry.Final is not null) throw new JsonException();
                records.Add(entry.Kind == ProviderInputKind.HostInstruction ? ProviderInput.Instruction(entry.Text) : ProviderInput.Data(entry.Text));
            }
            else if (entry.Kind == ProviderInputKind.ModelData && entry.Final is { } final && entry.Text is null)
            {
                var origins = records.Where(r => r.Model is not null).Select(r => r.Model!.Attempt).ToArray();
                var original = new ProviderRequest(config.Scope, final.Attempt, records, config.Tools.Select(t => t.Descriptor).ToArray(), continuation,
                    options.RequireContinuation ? ProviderCapabilities.Continuation : ProviderCapabilities.None, config.Bounds, new(origins));
                var model = ProviderResponse.RestoreFinal(original, new(final.Scope, final.Attempt, ProviderFinish.Final, final.Text, [], final.Continuation?.Restore()));
                records.Add(ProviderInput.FromModel(model)); continuation = model.Continuation;
            }
            else throw new JsonException();
        }
        return records;
    }
    private static void Validate(OrdinaryCheckpoint saved, RuntimeConfiguration config, RuntimeOptions options)
    {
        if (!saved.Scope.Matches(config.Scope) || saved.Binding != Binding(config, options)) throw new ContextRejected(ContextRejectionCode.IncompatibleContext);
        if (saved.Records.Length is < 1 || saved.Records.Length > options.MaximumRecords || saved.Records[0].Kind != ProviderInputKind.HostInstruction
            || saved.Records.Skip(1).Any(r => r.Kind == ProviderInputKind.HostInstruction) || saved.Rounds.Length is < 1 or > 64
            || saved.Elapsed < TimeSpan.Zero || saved.Final && saved.RetryEligible) throw new JsonException();
        var executions = new HashSet<Guid>(); var rounds = new HashSet<Guid>(); var physical = new Dictionary<Guid, UsageAttemptObservation>();
        var finals = new Dictionary<Guid, SavedAttempt>();
        SavedRound? previous = null;
        foreach (var round in saved.Rounds)
        {
            if (round.LogicalWorkId == Guid.Empty || round.RoundId == Guid.Empty || !executions.Add(round.Usage.ExecutionId)
                || !rounds.Add(round.RoundId) || round.Usage.Coverage != UsageInventoryCoverage.Complete
                || !Enum.IsDefined(round.Reason) || !Enum.IsDefined(round.Stop) || round.Completed < 0
                || round.Facts.Length != round.Usage.Attempts.Count || round.Completed != round.Facts.Count(f => f.AcceptedFinal)
                || round.Usage.ToolInvocations is not { Invoked: 0, ReservedUnstarted: 0, ReleasedUnstarted: 0 }) throw new JsonException();
            if (round.Usage.ContinuedCalls.Count > 1 || previous is null && round.Usage.ContinuedCalls.Count != 0
                || previous is not null && (round.Usage.ContinuedCalls.Count == 1
                    ? round.LogicalWorkId != previous.LogicalWorkId : round.LogicalWorkId == previous.LogicalWorkId)) throw new JsonException();
            if (previous is not null && round.Usage.ContinuedCalls.Count == 0
                && previous.Facts.LastOrDefault() is not { AcceptedFinal: true, SettlementStop: RuntimeStop.None or RuntimeStop.HostStopped }) throw new JsonException();
            foreach (var seed in round.Usage.ContinuedCalls)
                if (!physical.TryGetValue(seed.PhysicalAttemptId, out var prior) || prior.ExecutionId != seed.ExecutionId
                    || prior.LogicalCallId != seed.LogicalCallId || prior.AttemptNumber != seed.AttemptNumber
                    || previous?.Facts.LastOrDefault() is not { Retry: not null, SettlementStop: RuntimeStop.None, Outcome: ProviderOutcome.Failed } predecessor
                    || !predecessor.Attempt.Matches(new(seed.ExecutionId, seed.LogicalCallId, seed.PhysicalAttemptId, seed.AttemptNumber))) throw new JsonException();
            foreach (var observation in round.Usage.Attempts)
            {
                if (!physical.TryAdd(observation.PhysicalAttemptId, observation)) throw new JsonException();
                var matches = round.Facts.Where(f => f.Attempt.ExecutionId == observation.ExecutionId && f.Attempt.LogicalCallId == observation.LogicalCallId
                    && f.Attempt.PhysicalAttemptId == observation.PhysicalAttemptId && f.Attempt.AttemptNumber == observation.AttemptNumber).ToArray();
                if (matches.Length != 1) throw new JsonException();
                var fact = matches[0];
                if (!Enum.IsDefined(fact.Stop) || !Enum.IsDefined(fact.SettlementStop) || fact.Outcome.HasValue && !Enum.IsDefined(fact.Outcome.Value)
                    || fact.Error.HasValue && !Enum.IsDefined(fact.Error.Value)
                    || fact.Retry is not null && (fact.Outcome != ProviderOutcome.Failed || fact.Error != ProviderError.ProviderFailed
                        || fact.Stop != RuntimeStop.None || fact.SettlementStop != RuntimeStop.None)
                    || fact.AcceptedFinal && (fact.Outcome != ProviderOutcome.Succeeded || fact.Error != ProviderError.None)) throw new JsonException();
                if (fact.AcceptedFinal) finals.Add(observation.PhysicalAttemptId, fact);
            }
            if (!round.Facts.Select(f => f.Attempt.PhysicalAttemptId).SequenceEqual(round.Usage.Attempts.Select(a => a.PhysicalAttemptId))) throw new JsonException();
            previous = round;
        }
        if (physical.Count > options.MaximumAttempts) throw new JsonException();
        var current = saved.Rounds[^1];
        if (saved.Info != new ContextCheckpointInfo(saved.Info.CheckpointId, current.LogicalWorkId, current.RoundId, current.Usage.ExecutionId)
            || saved.Info.CheckpointId == Guid.Empty) throw new JsonException();
        var modelIds = saved.Records.Where(r => r.Final is not null).Select(r => r.Final!.Attempt.PhysicalAttemptId).ToArray();
        if (modelIds.Length != finals.Count || modelIds.Distinct().Count() != modelIds.Length || modelIds.Any(id => !finals.ContainsKey(id))) throw new JsonException();
        var represented = saved.Records.Where(r => r.Final is not null).Select(r => r.Final!).ToArray();
        if (!represented.Select(f => f.Attempt.PhysicalAttemptId).SequenceEqual(finals.Keys)
            || represented.Any(f => !f.Attempt.Matches(finals[f.Attempt.PhysicalAttemptId].Attempt) || !f.Scope.Matches(saved.Scope))) throw new JsonException();
        var last = current.Facts.LastOrDefault();
        if (saved.Final != (last is { AcceptedFinal: true, SettlementStop: RuntimeStop.None or RuntimeStop.HostStopped }) || saved.RetryEligible != (last is { Retry: not null, AcceptedFinal: false }
            && last.SettlementStop == RuntimeStop.None && current.Usage.Attempts[^1].Exposure == DispatchExposure.Dispatched)
            || last is not null && saved.OriginalBounds is null) throw new JsonException();
    }
}
internal sealed class ContextRejected(ContextRejectionCode code) : Exception
{
    internal ContextRejectionCode Code { get; } = code;
}
