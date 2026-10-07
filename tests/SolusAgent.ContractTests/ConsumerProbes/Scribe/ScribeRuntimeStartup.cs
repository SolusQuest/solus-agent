using System.Text.Json;
using SolusAgent.Api.Execution;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ConsumerProbes.ScribeHost;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;

namespace SolusAgent.ContractTests.ConsumerProbes.Scribe;

/// <summary>Restricted record of one startup production; raw exchange, response and tool results stay out of ordinary diagnostics.</summary>
internal sealed record ScribeProductionRecord(ConfigurationAttempt Attempt, ProviderRequest Exchange, List<ToolResult> ToolResults);

/// <summary>
/// Test startup composition of the actual ConfigurationProvider, ConfigurationHooks, ConfigurationConsumer and installed
/// ConfigurationTool. The consumer performs exposure, permission, provider dispatch and settlement; the startup production
/// path then performs real guarded tool preparation and invocation against the correlated provider calls. Restricted
/// provider responses, continuations and raw attempt records stay here, and only closed diagnostics leave this object.
/// </summary>
internal sealed class ScribeRuntimeStartup
{
    internal const string ContinuationCanary = "RESTRICTED_CONTINUATION_CANARY replay bytes";
    internal const string ToolName = "configured_echo";

    private readonly List<ScribeProductionRecord> productions = [];

    internal ScribeRuntimeStartup(AgentRequest request, bool captureUsage = true)
    {
        Request = request;
        Provider = new ConfigurationProvider { CaptureUsage = captureUsage };
        Hooks = new ConfigurationHooks();
        Capability = new ConfigurationCapability();
        Tool = new ConfigurationTool();
        Binding = new RuntimeToolRegistration(Tool, Capability);
        Configuration = new SolusAgent.Runtime.Api.Configuration.RuntimeConfiguration(Provider, [Binding], Hooks);
        Consumer = new ConfigurationConsumer(Configuration, request);
        Provider.AfterEffect = () => Hooks.Record("dispatch");
    }

    internal AgentRequest Request { get; }
    internal ConfigurationProvider Provider { get; }
    internal ConfigurationHooks Hooks { get; }
    internal ConfigurationCapability Capability { get; }
    internal ConfigurationTool Tool { get; }
    internal RuntimeToolRegistration Binding { get; }
    internal SolusAgent.Runtime.Api.Configuration.RuntimeConfiguration Configuration { get; }
    internal ConfigurationConsumer Consumer { get; }
    internal IReadOnlyList<ScribeProductionRecord> Productions => productions.ToArray();

    /// <summary>Optional restricted response script for negative startup neighbors; unset uses the echo production.</summary>
    internal Func<ProviderRequest, ProviderResponse>? ProductionResponse { get; set; }

    /// <summary>Gets only closed attempt diagnostics, never payloads, continuations, credentials or raw responses.</summary>
    internal IReadOnlyList<RuntimeAttemptDiagnostic> Diagnostics => productions.Select(record => record.Attempt.Diagnostic).ToArray();

    /// <summary>Runs one real configuration exchange on a fresh logical call and retains its restricted record.</summary>
    internal ValueTask<ConfigurationAttempt> RunAttemptAsync(ProviderAttempt attempt, bool requireContinuation = false,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(attempt, new List<ToolResult>(), requireContinuation, cancellationToken);

    /// <summary>
    /// Produces one candidate through the actual configuration exchange and installed tool. The produced fact text is
    /// extracted from the real guarded tool result, so provider or tool failure prevents candidate production entirely.
    /// </summary>
    internal async ValueTask<string> ProduceCandidateAsync(string member, string fact, CancellationToken cancellationToken = default)
    {
        var argumentsJson = JsonSerializer.Serialize(new { text = fact });
        Provider.Response = ProductionResponse ?? (request => new ProviderResponse(Provider.Scope, request.Attempt, ProviderFinish.ToolCalls,
            ConfigurationProvider.ModelCanary, [new ToolCall("call-1", ToolName, argumentsJson)]));
        var toolResults = new List<ToolResult>();
        var attempt = new ProviderAttempt(Request.ExecutionId, Guid.NewGuid(), Guid.NewGuid());
        var result = await RunCoreAsync(attempt, toolResults, false, cancellationToken).ConfigureAwait(false);
        if (result.AdmissionStop == RuntimeStop.Cancelled || result.Provider?.Outcome == ProviderOutcome.Cancelled
            || cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (result.AdmissionStop != RuntimeStop.None || result.Provider?.Outcome != ProviderOutcome.Succeeded
            || result.Provider.Response is null)
        {
            throw new InvalidOperationException("The configuration exchange prevented candidate production.");
        }

        // Provider and model data cannot select bindings: only the Host-installed tool is prepared or invoked.
        foreach (var call in result.Provider.Response.Calls)
        {
            if (call.ToolName != Tool.Descriptor.Name)
            {
                throw new InvalidOperationException("Provider or model data cannot select tool bindings.");
            }

            var preparation = Tool.Prepare(call);
            if (!preparation.Accepted || preparation.Prepared is null)
            {
                throw new InvalidOperationException("Installed tool preparation failed.");
            }

            var toolResult = await Tool.InvokeAsync(preparation.Prepared, call, Capability, cancellationToken).ConfigureAwait(false);
            if (toolResult.Outcome != ToolOutcome.Succeeded || !toolResult.Call.Matches(call))
            {
                throw new InvalidOperationException("Installed tool invocation failed or drifted.");
            }

            toolResults.Add(toolResult);
        }

        if (toolResults.Count != 1)
        {
            throw new InvalidOperationException("The synthetic production expects exactly one correlated tool result.");
        }

        return ScribeCandidatePayload.Format(member, ExtractEchoText(toolResults[0]));
    }

    /// <summary>Runs one attempt whose provider response carries restricted continuation replay bytes for inspection.</summary>
    internal ValueTask<ConfigurationAttempt> RunContinuationAttemptAsync(ProviderAttempt attempt, string continuationCanary,
        CancellationToken cancellationToken = default)
    {
        Provider.Response = request => new ProviderResponse(Provider.Scope, request.Attempt, ProviderFinish.Final,
            ConfigurationProvider.ModelCanary, [],
            new ProviderContinuation(Provider.Scope, request.Attempt, System.Text.Encoding.UTF8.GetBytes(continuationCanary)));
        return RunCoreAsync(attempt, new List<ToolResult>(), requireContinuation: true, cancellationToken);
    }

    private async ValueTask<ConfigurationAttempt> RunCoreAsync(ProviderAttempt attempt, List<ToolResult> toolResults,
        bool requireContinuation, CancellationToken cancellationToken)
    {
        // The standard path consumes the consumer's own AgentRequest projection; the restricted continuation
        // neighbor replays the same projection through the configuration factory with an explicit replay requirement.
        var exchange = requireContinuation
            ? Configuration.CreateRequest(attempt, ProjectInputs(), requiredCapabilities: ProviderCapabilities.Continuation)
            : Consumer.CreateRequest(attempt);
        var result = await Consumer.RunAsync(exchange, cancellationToken).ConfigureAwait(false);
        productions.Add(new ScribeProductionRecord(result, exchange, toolResults));
        return result;
    }

    private IReadOnlyList<ProviderInput> ProjectInputs()
    {
        var inputs = new List<ProviderInput> { ProviderInput.Instruction(Request.Instructions) };
        foreach (var item in Request.Data)
        {
            inputs.Add(ProviderInput.Data(item.Text));
        }

        return inputs;
    }

    private static string ExtractEchoText(ToolResult result)
    {
        using var document = JsonDocument.Parse(result.Json!);
        return document.RootElement.GetProperty("text").GetString()!;
    }
}
