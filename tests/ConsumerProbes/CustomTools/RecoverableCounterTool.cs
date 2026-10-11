using System.Collections.Concurrent;
using System.Text.Json;
using SolusAgent.Tools.Api;

namespace CustomTools;

/// <summary>Controlled query branches of the independently compiled producer.</summary>
public enum RecoveryQueryBehavior { Normal, Unresolved, WrongOperation, WrongCall, InvalidJson, Oversized, PriorFailure, Throw }

/// <summary>A test-owned keyed target with optional cross-process file retention, never a runtime-owned store.</summary>
public sealed class RecoveryCounterCapability(string targetSnapshot, ToolRecoveryGuarantee guarantee,
    bool mutates = true, string? storePath = null) : IToolCapability
{
    private sealed record Entry(string CallId, string ToolName, string Arguments, string Json);
    private sealed record Store(string Target, long Total, int Effects, Dictionary<Guid, Entry> Entries);
    private readonly object gate = new();
    private Store memory = new(targetSnapshot, 0, 0, []);
    private int invocations; private int queries;
    /// <inheritdoc />
    public string CapabilityId => "recovery_operation";
    /// <summary>The actual injected target snapshot.</summary>
    public string TargetSnapshot { get; } = targetSnapshot;
    /// <summary>The independently implemented guarantee.</summary>
    public ToolRecoveryGuarantee Guarantee { get; } = guarantee;
    /// <summary>Current permission, rechecked at entry.</summary>
    public bool Allowed { get; set; } = true;
    /// <summary>Actual operation interface entries in this process.</summary>
    public int Invocations => Volatile.Read(ref invocations);
    /// <summary>Actual query entries in this process.</summary>
    public int Queries => Volatile.Read(ref queries);
    /// <summary>Target mutations, including mutations preceding failure.</summary>
    public int Effects => Access(s => (s, s.Effects));
    /// <summary>Current retained target value.</summary>
    public long Total => Access(s => (s, s.Total));
    /// <summary>Controlled producer failure after the keyed effect is retained.</summary>
    public bool ThrowAfterEffect { get; set; }
    /// <summary>Controlled producer failure result after the effect.</summary>
    public bool FailAfterEffect { get; set; }
    /// <summary>Controlled asynchronous return boundary, allowing an outstanding original call.</summary>
    public Func<ToolOperation, CancellationToken, ValueTask>? BeforeReturn { get; set; }
    /// <summary>Controlled asynchronous query boundary.</summary>
    public Func<ToolOperation, CancellationToken, ValueTask>? BeforeQuery { get; set; }
    /// <summary>Controls query evidence without changing invocation semantics.</summary>
    public RecoveryQueryBehavior QueryBehavior { get; set; }
    /// <summary>Signals actual original/repeat entry.</summary>
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Signals actual query entry.</summary>
    public TaskCompletionSource QueryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal async ValueTask<ToolOutput> Invoke(ToolOperation operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Allowed) throw new InvalidOperationException("Permission denied.");
        Interlocked.Increment(ref invocations);
        var json = Access(store =>
        {
            if (store.Entries.TryGetValue(operation.OperationId, out var prior))
            {
                if (prior.CallId != operation.Call.CallId || prior.ToolName != operation.Call.ToolName || prior.Arguments != operation.Call.ArgumentsJson)
                    throw new InvalidOperationException("Key association mismatch.");
                return (store, prior.Json);
            }
            using var arguments = JsonDocument.Parse(operation.Call.ArgumentsJson);
            var total = mutates ? checked(store.Total + arguments.RootElement.GetProperty("amount").GetInt64()) : store.Total;
            var result = JsonSerializer.Serialize(new { total });
            var entries = new Dictionary<Guid, Entry>(store.Entries) { [operation.OperationId] = new(operation.Call.CallId,
                operation.Call.ToolName, operation.Call.ArgumentsJson, result) };
            return (store with { Total = total, Effects = store.Effects + (mutates ? 1 : 0), Entries = entries }, result);
        });
        Entered.TrySetResult();
        if (BeforeReturn is { } wait) await wait(operation, token).ConfigureAwait(false);
        if (ThrowAfterEffect) throw new InvalidOperationException("RECOVERY_EXCEPTION_CANARY");
        return FailAfterEffect ? ToolOutput.Failure(operation.Call) : ToolOutput.Success(operation.Call, json);
    }
    internal async ValueTask<ToolRecoveryReceipt> Query(ToolOperation operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Allowed) throw new InvalidOperationException("Permission denied.");
        Interlocked.Increment(ref queries); QueryEntered.TrySetResult();
        if (BeforeQuery is { } wait) await wait(operation, token).ConfigureAwait(false);
        if (QueryBehavior == RecoveryQueryBehavior.Throw) throw new InvalidOperationException("QUERY_EXCEPTION_CANARY");
        var entry = Access(s => (s, s.Entries.GetValueOrDefault(operation.OperationId)));
        if (entry is null || QueryBehavior == RecoveryQueryBehavior.Unresolved) return ToolRecoveryReceipt.Unresolved(operation);
        var recorded = new ToolCall(entry.CallId, entry.ToolName, entry.Arguments);
        var associated = QueryBehavior == RecoveryQueryBehavior.WrongOperation ? new ToolOperation(Guid.NewGuid(), operation.Call, operation.Recovery) : operation;
        if (QueryBehavior == RecoveryQueryBehavior.WrongCall) recorded = new("foreign", recorded.ToolName, recorded.ArgumentsJson);
        if (QueryBehavior == RecoveryQueryBehavior.PriorFailure)
            return ToolRecoveryReceipt.Found(associated, recorded, ToolOutcome.Failed, ToolError.InvocationFailed, true);
        return ToolRecoveryReceipt.Found(associated, recorded, ToolOutcome.Succeeded, ToolError.None, true,
            QueryBehavior == RecoveryQueryBehavior.InvalidJson ? "{\"total\":\"QUERY_CANARY\"}"
                : QueryBehavior == RecoveryQueryBehavior.Oversized ? new string(' ', 101) + entry.Json : entry.Json);
    }
    private T Access<T>(Func<Store, (Store Updated, T Value)> operation)
    {
        lock (gate)
        {
            if (storePath is null)
            {
                var (updated, value) = operation(memory); memory = updated; return value;
            }
            // A bounded synthetic target lock coordinates actual overlapping child processes.
            FileStream? stream = null;
            var until = DateTime.UtcNow.AddSeconds(5);
            while (stream is null)
                try { stream = new(storePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) when (DateTime.UtcNow < until) { Thread.Sleep(10); }
            using (stream)
            {
                var saved = stream.Length == 0 ? new Store(TargetSnapshot, 0, 0, []) : JsonSerializer.Deserialize<Store>(stream)!;
                if (saved.Target != TargetSnapshot) throw new InvalidOperationException("Actual target changed.");
                var (updated, value) = operation(saved);
                stream.Position = 0; stream.SetLength(0); JsonSerializer.Serialize(stream, updated); stream.Flush(true); return value;
            }
        }
    }
}

/// <summary>Interface-authored recovery producer using the real FunctionTool guard for every fresh prepared handle.</summary>
public sealed class RecoverableCounterTool(int maximumResultBytes = 100, ToolEffect effect = ToolEffect.Mutating) : IRecoverableFunctionTool
{
    private sealed class Invocation(ToolDescriptor descriptor) : FunctionTool<RecoveryCounterCapability>(descriptor)
    {
        internal ToolOperation? Operation;
        protected override ValueTask<ToolOutput> InvokeCoreAsync(ToolCall call, RecoveryCounterCapability capability, CancellationToken token) =>
            Operation is { } operation ? capability.Invoke(operation, token) : ValueTask.FromResult(ToolOutput.Failure(call));
    }
    private readonly ConcurrentDictionary<PreparedToolInvocation, Invocation> handles = new();
    /// <inheritdoc />
    public ToolDescriptor Descriptor { get; } = new("recover", "Synthetic keyed recovery", ToolSchema.Parse(CounterTool.InputSchema),
        ToolSchema.Parse(CounterTool.ResultSchema), "recovery_operation", effect, maximumResultBytes: maximumResultBytes);
    /// <inheritdoc />
    public ToolPreparation Prepare(ToolCall call)
    {
        if (handles.Count >= 64) throw new ToolContractException(ToolError.LimitExceeded);
        var guarded = new Invocation(Descriptor); var prepared = guarded.Prepare(call);
        if (prepared.Prepared is { } handle) handles.TryAdd(handle, guarded);
        return prepared;
    }
    /// <inheritdoc />
    public ToolError ValidateInvocation(PreparedToolInvocation prepared, ToolCall call, IToolCapability? capability) =>
        handles.TryGetValue(prepared, out var guarded) ? guarded.ValidateInvocation(prepared, call, capability) : ToolError.PreparedMismatch;
    /// <inheritdoc />
    public ValueTask<ToolResult> InvokeAsync(PreparedToolInvocation prepared, ToolCall call, IToolCapability? capability, CancellationToken token = default) =>
        (handles.GetValueOrDefault(prepared) ?? new Invocation(Descriptor)).InvokeAsync(prepared, call, capability, token);
    /// <inheritdoc />
    public ToolError ValidateOperation(ToolOperation operation, IToolCapability? capability) => capability is RecoveryCounterCapability { Allowed: true } target
        && operation.Call.ToolName == Descriptor.Name && operation.Recovery is { BindingId: "keyed_recovery" } binding
        && binding.TargetSnapshot == target.TargetSnapshot && binding.Guarantee == target.Guarantee ? ToolError.None : ToolError.UnsupportedCapability;
    /// <inheritdoc />
    public ValueTask<ToolResult> InvokeOperationAsync(PreparedToolInvocation prepared, ToolOperation operation, IToolCapability? capability,
        CancellationToken token = default)
    {
        if (!handles.TryGetValue(prepared, out var guarded)) return new Invocation(Descriptor).InvokeAsync(prepared, operation.Call, capability, token);
        if (ValidateOperation(operation, capability) != ToolError.None) return guarded.InvokeAsync(prepared, operation.Call, null, token);
        var prior = Interlocked.CompareExchange(ref guarded.Operation, operation, null);
        if (prior is not null && !prior.Matches(operation)) return guarded.InvokeAsync(prepared, operation.Call, null, token);
        return guarded.InvokeAsync(prepared, operation.Call, capability, token);
    }
    /// <inheritdoc />
    public ValueTask<ToolRecoveryReceipt> QueryOutcomeAsync(ToolOperation operation, IToolCapability? capability, CancellationToken token = default)
    {
        if (ValidateOperation(operation, capability) != ToolError.None || operation.Recovery!.Guarantee != ToolRecoveryGuarantee.QueryOutcome)
            throw new ToolContractException(ToolError.UnsupportedCapability);
        return ((RecoveryCounterCapability)capability!).Query(operation, token);
    }
}
