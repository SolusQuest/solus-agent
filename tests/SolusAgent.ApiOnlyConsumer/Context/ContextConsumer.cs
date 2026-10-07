using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;

namespace SolusAgent.ApiOnlyConsumer.Context;

/// <summary>Actual Api-only Host consumption, with ordinary observations separate from a supplied restricted sink.</summary>
public static class ContextConsumer
{
    public static async ValueTask<ObservedContextExecution> RunAsync(IContextAgent agent, ContextExecutionRequest request,
        IRestrictedContextSink? sink = null, CancellationToken cancellationToken = default)
    {
        var progress = new CollectingProgress();
        var result = await agent.ExecuteWithContextAsync(request, sink, progress, cancellationToken);
        return new ObservedContextExecution(progress.Values.AsReadOnly(), result);
    }

    private sealed class CollectingProgress : IProgress<AgentProgress>
    {
        internal List<AgentProgress> Values { get; } = [];
        public void Report(AgentProgress value) => Values.Add(value);
    }
}

public sealed record ObservedContextExecution(IReadOnlyList<AgentProgress> Progress, ContextExecutionResult Result);

/// <summary>Test-only Host-owned retention, explicitly outside the ordinary consumer result.</summary>
public sealed class RestrictedContextHost : IRestrictedContextSink
{
    private readonly object sync = new();
    private AgentContextEnvelope? context;
    private int captureCount;
    public int CaptureCount => Volatile.Read(ref captureCount);

    public void Capture(AgentContextEnvelope value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (sync)
        {
            context = new AgentContextEnvelope(value.ImplementationId, value.FormatVersion, value.CompatibilityVersion, value.CopyRestrictedPayload());
            Interlocked.Increment(ref captureCount);
        }
    }

    public AgentContextEnvelope CopyRestrictedContext()
    {
        lock (sync)
        {
            if (context is null) throw new InvalidOperationException("No restricted capture is available.");
            return new AgentContextEnvelope(context.ImplementationId, context.FormatVersion, context.CompatibilityVersion, context.CopyRestrictedPayload());
        }
    }

    public override string ToString() => $"RestrictedContextHost {{ CaptureCount = {CaptureCount} }}";
}
