using CustomTools;
using SolusAgent.Api.Usage;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;

namespace SolusAgent.ContractTests.ConsumerProbes.Apr;

/// <summary>Requested provider measurement behavior for one synthetic scenario startup.</summary>
internal enum AprUsageMode
{
    /// <summary>The guarded provider reports complete known measurements.</summary>
    Complete,

    /// <summary>The guarded provider reports only a partial measurement.</summary>
    Partial,

    /// <summary>The guarded provider reports no measurement.</summary>
    Unavailable,
}

/// <summary>Finite test-only scenario policy; none of these values is production enforcement or storage.</summary>
internal sealed class AprStartupOptions
{
    /// <summary>Gets the finite candidate production goal; completion is distinct from Host acceptance.</summary>
    public int TargetProductions { get; init; } = 3;

    /// <summary>Gets the finite ordinary and context work-unit goal.</summary>
    public int TargetWorkUnits { get; init; } = 1;

    /// <summary>Gets an optional partial-stop boundary for context and ordinary work.</summary>
    public int? PartialAfterWorkUnits { get; init; }

    /// <summary>Gets the default synthetic step amount when no step data is supplied.</summary>
    public int StepAmount { get; init; } = 2;

    /// <summary>Gets an optional provider-scripted tool argument override, used for negative admission cases.</summary>
    public int? ToolCallAmountOverride { get; init; }

    /// <summary>Gets an optional second scripted tool call amount, used for whole-batch admission cases.</summary>
    public int? SecondCallAmount { get; init; }

    /// <summary>Gets the actual custom tool's controlled output mode.</summary>
    public CounterMode ToolMode { get; init; } = CounterMode.Normal;

    /// <summary>Gets the guarded provider measurement behavior.</summary>
    public AprUsageMode Usage { get; init; } = AprUsageMode.Complete;

    /// <summary>Gets whether startup registers a metadata-matching foreign capability for admission negatives.</summary>
    public bool UseForeignCapability { get; init; }
}

/// <summary>
/// Finite test-only provider script standing in for model behavior at the actual guarded custom
/// provider seam. It turns classified untrusted data into real tool call requests and derives its
/// final text from actual guarded tool results, while recording every model-visible text and tool
/// argument for confinement checks.
/// </summary>
internal sealed class AprProviderScript(ProviderScope scope, AprStartupOptions options)
{
    /// <summary>Gets the synthetic step-data prefix the script reads as untrusted input.</summary>
    public const string StepPrefix = "apr-step: amount=";

    private readonly object gate = new();
    private readonly List<string> modelVisibleTexts = [];
    private readonly List<string> toolArguments = [];
    private readonly List<IReadOnlyList<string>> toolDefinitions = [];
    private int callIds;

    /// <summary>Gets every text the provider seam observed as model-visible input.</summary>
    public IReadOnlyList<string> ModelVisibleTexts
    {
        get
        {
            lock (gate)
            {
                return modelVisibleTexts.ToArray();
            }
        }
    }

    /// <summary>Gets every tool argument JSON the provider script produced.</summary>
    public IReadOnlyList<string> ToolArguments
    {
        get
        {
            lock (gate)
            {
                return toolArguments.ToArray();
            }
        }
    }

    /// <summary>Gets every tool definition name set the provider seam observed on a request.</summary>
    public IReadOnlyList<IReadOnlyList<string>> ToolDefinitions
    {
        get
        {
            lock (gate)
            {
                return toolDefinitions.ToArray();
            }
        }
    }

    /// <summary>Produces one guarded provider response for the current classified exchange.</summary>
    public ProviderResponse Respond(ProviderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            foreach (var input in request.Inputs)
            {
                if (input.Text is not null)
                {
                    modelVisibleTexts.Add(input.Text);
                }

                if (input.Model?.Text is not null)
                {
                    modelVisibleTexts.Add(input.Model.Text);
                }

                if (input.ToolResult?.Json is not null)
                {
                    modelVisibleTexts.Add(input.ToolResult.Json);
                }
            }

            toolDefinitions.Add(request.Tools.Select(tool => tool.Name).ToArray());
        }

        var completedToolRound = request.Inputs.Any(input => input.Kind == ProviderInputKind.ToolResultData);
        if (!completedToolRound)
        {
            var amount = options.ToolCallAmountOverride ?? ParseStepAmount(request) ?? options.StepAmount;
            var calls = new List<ToolCall> { NewCall(amount) };
            if (options.SecondCallAmount is int second)
            {
                calls.Add(NewCall(second));
            }

            return new ProviderResponse(scope, request.Attempt, ProviderFinish.ToolCalls, "apr-model: step", calls);
        }

        var total = ParseToolTotals(request);
        return new ProviderResponse(scope, request.Attempt, ProviderFinish.Final, $"apr-model: total={total}", []);
    }

    private ToolCall NewCall(int amount)
    {
        var arguments = $"{{\"amount\":{amount}}}";
        lock (gate)
        {
            toolArguments.Add(arguments);
        }

        return new ToolCall($"apr-call-{++callIds}", "counter", arguments);
    }

    private static int? ParseStepAmount(ProviderRequest request)
    {
        foreach (var input in request.Inputs)
        {
            if (input.Text is not null && input.Text.StartsWith(StepPrefix, StringComparison.Ordinal)
                && int.TryParse(input.Text[StepPrefix.Length..], out var amount))
            {
                return amount;
            }
        }

        return null;
    }

    private static long ParseToolTotals(ProviderRequest request)
    {
        var total = 0L;
        foreach (var input in request.Inputs)
        {
            if (input.ToolResult?.Json is not { } json)
            {
                continue;
            }

            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("total", out var value))
            {
                total += value.GetInt64();
            }
        }

        return total;
    }
}

/// <summary>
/// Test-only startup composition: actual CustomTools counter tool and narrow capability, one actual
/// guarded CustomProvider provider, the existing Runtime.Api configuration and Host exposure hooks,
/// and the finite APR scenario wired into the Api-only business Host over outer interfaces only.
/// </summary>
internal sealed class AprStartup
{
    private AprStartup(IToolCapability capability, CounterTool tool, IModelProvider provider, ConfigurationHooks hooks,
        SolusAgent.Runtime.Api.Configuration.RuntimeConfiguration configuration, AprScenarioAgent scenario, AprHost.AprBusinessHost host, AprProviderScript script)
    {
        Capability = capability;
        Tool = tool;
        Provider = provider;
        Hooks = hooks;
        Configuration = configuration;
        Scenario = scenario;
        Host = host;
        Script = script;
    }

    /// <summary>Gets the actual synthetic narrow tool capability.</summary>
    public IToolCapability Capability { get; }

    /// <summary>Gets the actual custom tool instance.</summary>
    public CounterTool Tool { get; }

    /// <summary>Gets the actual guarded custom provider.</summary>
    public IModelProvider Provider { get; }

    /// <summary>Gets the existing Runtime.Api Host exposure hooks.</summary>
    public ConfigurationHooks Hooks { get; }

    /// <summary>Gets the live Host-supplied runtime configuration.</summary>
    public SolusAgent.Runtime.Api.Configuration.RuntimeConfiguration Configuration { get; }

    /// <summary>Gets the finite scenario implementation injected into the Host.</summary>
    public AprScenarioAgent Scenario { get; }

    /// <summary>Gets the Api-only business Host built over outer interfaces.</summary>
    public AprHost.AprBusinessHost Host { get; }

    /// <summary>Gets the provider script's model-visible capture.</summary>
    public AprProviderScript Script { get; }

    /// <summary>Gets actual guarded provider effect or invocation counts for effect assertions.</summary>
    public int ProviderEffects => Provider switch
    {
        ConfigurationProvider configured => configured.Effects,
        DelegateProvider delegated => delegated.Invocations,
        _ => 0,
    };

    /// <summary>Gets actual synthetic tool effect counts observed through the registered capability.</summary>
    public int ToolEffects => Capability switch
    {
        CounterCapability counter => counter.Effects,
        AprForeignCapability foreign => foreign.Effects,
        _ => 0,
    };

    /// <summary>Gets the current synthetic counter total derived from actual guarded tool output.</summary>
    public long ToolTotal => Capability is CounterCapability counter ? counter.Total : 0;

    /// <summary>Composes one finite scenario startup with the requested fixture behaviors.</summary>
    public static AprStartup Create(AprStartupOptions? options = null)
    {
        options ??= new AprStartupOptions();
        var scope = new ProviderScope("synthetic", "apr-model");
        var script = new AprProviderScript(scope, options);
        IModelProvider provider;
        switch (options.Usage)
        {
            case AprUsageMode.Partial:
                provider = new DelegateProvider(scope, (request, observation, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    observation.ObserveDispatch(DispatchExposure.Dispatched);
                    observation.CaptureUsage(new UsageObservation(inputTokens: 11));
                    return ValueTask.FromResult(script.Respond(request));
                });
                break;
            case AprUsageMode.Unavailable:
                provider = new ConfigurationProvider(scope) { Response = script.Respond, CaptureUsage = false };
                break;
            default:
                provider = new ConfigurationProvider(scope) { Response = script.Respond };
                break;
        }

        IToolCapability capability = options.UseForeignCapability ? new AprForeignCapability() : new CounterCapability();
        var tool = new CounterTool(options.ToolMode);
        var hooks = new ConfigurationHooks();
        var configuration = new SolusAgent.Runtime.Api.Configuration.RuntimeConfiguration(provider, [new RuntimeToolRegistration(tool, capability)], hooks,
            requiredAcknowledgement: ExposureStrength.Volatile,
            requiredGuarantees: RuntimeGuarantee.OrderedExposure | RuntimeGuarantee.ProviderBounds);
        var scenario = new AprScenarioAgent(configuration, options);
        var host = new AprHost.AprBusinessHost(scenario, scenario, scenario);
        return new AprStartup(capability, tool, provider, hooks, configuration, scenario, host, script);
    }
}

/// <summary>
/// Test-only metadata-matching capability that is not the counter tool's concrete narrow capability,
/// used to prove concrete capability admission happens before any invocation.
/// </summary>
internal sealed class AprForeignCapability : IToolCapability
{
    private int effects;

    public string CapabilityId => "counter_increment";

    public int Effects => Volatile.Read(ref effects);

    public void Touch() => Interlocked.Increment(ref effects);
}
