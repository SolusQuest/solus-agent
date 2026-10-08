using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Providers;

namespace SolusAgent.ConsumerProbes.CustomProvider;

/// <summary>A finite independently compiled provider for production-runtime tests, separate from the M1 expressibility fixtures.</summary>
public sealed class ScriptedProvider : ModelProvider
{
    private readonly Func<ProviderRequest, ProviderObservation, CancellationToken, ValueTask<ProviderResponse>>[] steps;
    private readonly object gate = new();
    private readonly string credential = "SCRIPTED_PRIVATE_CREDENTIAL_CANARY";
    private int next;
    public ScriptedProvider(IReadOnlyList<Func<ProviderRequest, ProviderObservation, CancellationToken, ValueTask<ProviderResponse>>> steps,
        ProviderScope? scope = null, ProviderCapabilities capabilities = DelegateProvider.All) : base(scope ?? new("p", "m"), capabilities)
    {
        if (steps.Count is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(steps));
        this.steps = steps.ToArray();
        if (this.steps.Any(step => step is null)) throw new ArgumentException("Invalid synthetic script.");
    }
    public int Effects { get { lock (gate) return next; } }
    public ProviderRequest? LastRequest { get; private set; }
    protected override ValueTask<ProviderResponse> ExchangeCoreAsync(ProviderRequest request, ProviderObservation observation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Func<ProviderRequest, ProviderObservation, CancellationToken, ValueTask<ProviderResponse>> step;
        lock (gate)
        {
            if (credential.Length == 0 || next >= steps.Length) throw new InvalidOperationException("Synthetic script exhausted.");
            step = steps[next++]; LastRequest = request;
        }
        observation.ObserveDispatch(DispatchExposure.Dispatched);
        return step(request, observation, token);
    }
    public static ValueTask<ProviderResponse> Final(ProviderRequest request, ProviderObservation observation, CancellationToken token)
    {
        observation.CaptureUsage(new(3, 2));
        return ValueTask.FromResult(new ProviderResponse(request.Scope, request.Attempt, ProviderFinish.Final, "f", []));
    }
}

/// <summary>Independent interface seam for faults and guarded forwarding, without a Runtime reference.</summary>
public sealed class InterfaceScriptedProvider(ProviderScope scope,
    Func<ProviderRequest, CancellationToken, ValueTask<ProviderExchangeResult>> exchange) : IModelProvider
{
    private int calls;
    public ProviderScope Scope { get; } = scope;
    public ProviderCapabilities Capabilities => DelegateProvider.All;
    public int Calls => calls;
    public ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref calls) > 64) throw new InvalidOperationException("Synthetic interface script exhausted.");
        return exchange(request, cancellationToken);
    }
}
