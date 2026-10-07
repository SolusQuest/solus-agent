using System.Text.Json;
using SolusAgent.Tools.Api;

namespace CustomTools;

/// <summary>A synthetic narrow capability, containing no generic host services.</summary>
public sealed class CounterCapability(string capabilityId = "counter_increment") : IToolCapability
{
    private long total;
    private int effects;
    /// <summary>The explicitly supplied capability semantics.</summary>
    public string CapabilityId { get; } = capabilityId;
    /// <summary>Actual synthetic invocation effects, including effects preceding failure.</summary>
    public int Effects => Volatile.Read(ref effects);
    /// <summary>The current synthetic sum.</summary>
    public long Total => Interlocked.Read(ref total);
    /// <summary>The sole effect seam; cancellation is observed immediately before mutation.</summary>
    public long Add(long amount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref effects);
        return Interlocked.Add(ref total, amount);
    }
}

/// <summary>Controlled synthetic outputs used to exercise the real guarded consumer path.</summary>
public enum CounterMode
{
    /// <summary>Return a valid associated result.</summary>
    Normal,
    /// <summary>Throw after the effect.</summary>
    ThrowAfterEffect,
    /// <summary>Return a fixed failure after the effect.</summary>
    FailAfterEffect,
    /// <summary>Wait for matching cancellation after the effect.</summary>
    WaitAfterEffect,
    /// <summary>Produce a result for another call.</summary>
    WrongAssociation,
    /// <summary>Exceed the result byte limit.</summary>
    OversizedResult,
    /// <summary>Violate the result schema.</summary>
    InvalidResult,
    /// <summary>Throw cancellation for an unrelated token.</summary>
    UnrelatedCancellation,
    /// <summary>Signal cancellation after completion and return successful evidence.</summary>
    SignalCancellationThenSucceed,
    /// <summary>Produce invalid UTF-8 output after the effect.</summary>
    InvalidResultEncoding,
    /// <summary>Produce duplicate JSON result members.</summary>
    DuplicateResult,
    /// <summary>Produce malformed JSON output.</summary>
    MalformedResult,
    /// <summary>Change only the original argument association in the output.</summary>
    WrongArguments,
}

/// <summary>A real custom tool compiled against Tools.Api as its only production dependency.</summary>
public sealed class CounterTool : FunctionTool<CounterCapability>
{
    private readonly CounterMode mode;
    private readonly CancellationTokenSource? completionCancellation;
    /// <summary>Creates a synthetic producer with one bounded integer input and one integer result.</summary>
    public CounterTool(CounterMode mode = CounterMode.Normal, ToolEffect effect = ToolEffect.Mutating,
        int maximumArgumentBytes = ToolLimits.PayloadBytes, int maximumResultBytes = ToolLimits.PayloadBytes,
        CancellationTokenSource? completionCancellation = null)
        : base(new ToolDescriptor("counter", "Synthetic counter", ToolSchema.Parse(InputSchema),
            ToolSchema.Parse(ResultSchema), "counter_increment", effect, maximumArgumentBytes, maximumResultBytes))
    { this.mode = mode; this.completionCancellation = completionCancellation; }
    /// <summary>The supported synthetic argument descriptor.</summary>
    public const string InputSchema = "{\"type\":\"object\",\"properties\":{\"amount\":{\"type\":\"integer\"}},\"required\":[\"amount\"],\"additionalProperties\":false}";
    /// <summary>The supported synthetic result descriptor.</summary>
    public const string ResultSchema = "{\"type\":\"object\",\"properties\":{\"total\":{\"type\":\"integer\"}},\"required\":[\"total\"],\"additionalProperties\":false}";
    /// <summary>A deterministic signal after the effect, for in-flight cancellation/concurrent-use tests.</summary>
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <inheritdoc />
    protected override bool ValidateArguments(JsonElement arguments) => arguments.GetProperty("amount").GetInt64() is >= 0 and <= 100;
    /// <inheritdoc />
    protected override async ValueTask<ToolOutput> InvokeCoreAsync(ToolCall call, CounterCapability capability, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(call.ArgumentsJson);
        var total = capability.Add(document.RootElement.GetProperty("amount").GetInt64(), cancellationToken);
        Started.TrySetResult();
        switch (mode)
        {
            case CounterMode.ThrowAfterEffect: throw new InvalidOperationException("synthetic-private-detail");
            case CounterMode.FailAfterEffect: return ToolOutput.Failure(call);
            case CounterMode.WaitAfterEffect: await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); break;
            case CounterMode.WrongAssociation: return ToolOutput.Success(new("other", call.ToolName, call.ArgumentsJson), "{\"total\":0}");
            case CounterMode.OversizedResult: return ToolOutput.Success(call, new string(' ', ToolLimits.PayloadBytes + 1));
            case CounterMode.InvalidResult: return ToolOutput.Success(call, "{\"total\":\"wrong\"}");
            case CounterMode.InvalidResultEncoding: return ToolOutput.FromUtf8(call, [0xFF]);
            case CounterMode.DuplicateResult: return ToolOutput.Success(call, "{\"total\":1,\"total\":2}");
            case CounterMode.MalformedResult: return ToolOutput.Success(call, "{broken");
            case CounterMode.WrongArguments: return ToolOutput.Success(new(call.CallId, call.ToolName, "{\"amount\":2}"), "{\"total\":1}");
            case CounterMode.UnrelatedCancellation: throw new OperationCanceledException(new CancellationToken(true));
            case CounterMode.SignalCancellationThenSucceed: completionCancellation!.Cancel(); break;
        }
        return ToolOutput.Success(call, JsonSerializer.Serialize(new { total }));
    }
}
