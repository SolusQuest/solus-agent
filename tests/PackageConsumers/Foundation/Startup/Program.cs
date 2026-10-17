using System.Reflection;
using PackageConsumers.Foundation.Business;
using PackageConsumers.Foundation.CustomProvider;
using PackageConsumers.Foundation.CustomTool;
using SolusAgent.Providers.DeepSeek;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Startup;
using SolusAgent.Api.Candidates;
using SolusAgent.Runtime.Api.Providers;
using System.Text;
using System.Text.Json;

var provider = new TwoTurnProvider();
var counter = new Counter();
var hooks = new ExposureHooks();
var agent = RuntimeAgentFactory.Create(new(provider, [new(new IncrementTool(), counter)], hooks));
var outcome = await BusinessWorkflow.ExecuteAsync(agent);
if (counter.Effects != 1 || provider.Calls != 2 || hooks.Permissions != 2 || hooks.Closures != 2
    || outcome.Usage!.Attempts.Any(attempt => attempt.Usage.InputTokens != 3 || attempt.Usage.OutputTokens != 2))
    throw new InvalidOperationException("Public startup composition did not execute the expected effects and observations.");

var artifact = JsonSerializer.Serialize(new { value = new string('a', 1048576 - 12) });
var final = JsonSerializer.Serialize(new { value = new string('b', 1048576 - 12) });
if (Encoding.UTF8.GetByteCount(artifact) != 1048576 || Encoding.UTF8.GetByteCount(final) != 1048576)
    throw new InvalidOperationException("Synthetic full-size artifacts were not produced.");
var largeProvider = new LargeArtifactProvider(artifact, final); var largeCounter = new Counter();
var largeBounds = new ProviderExchangeBounds(maximumRequestBytes: ProviderLimits.MaximumRequestBytes,
    maximumResponseBytes: ProviderLimits.MaximumResponseBytes, maximumContinuationBytes: ProviderLimits.MaximumContinuationBytes,
    maximumTextBytes: ProviderLimits.MaximumTextBytes);
var largeAgent = RuntimeAgentFactory.Create(new(largeProvider, [new(new ArtifactTool(artifact), largeCounter)], new ExposureHooks(), largeBounds),
    new(maximumRetainedBytes: 16 * 1048576));
_ = await BusinessWorkflow.ExecuteLargeAsync((ICandidateAgent)largeAgent, final);
if (largeProvider.Calls != 2 || largeCounter.Effects != 1) throw new InvalidOperationException("Opt-in package effects differed.");

foreach (var attempts in new[] { 65, 128 })
{
    var retryProvider = new RetryCapacityProvider(attempts); var retryHooks = new ExposureHooks();
    var retryAgent = RuntimeAgentFactory.Create(new(retryProvider, [], retryHooks), new(maximumAttempts: 128));
    _ = await BusinessWorkflow.ExecuteRetriesAsync(retryAgent, attempts);
    if (retryProvider.Calls != attempts || retryHooks.Permissions != attempts || retryHooks.Closures != attempts)
        throw new InvalidOperationException("Package retry permission or settlement count differed.");
}

CheckReferences(typeof(BusinessWorkflow).Assembly, ["SolusAgent.Api"]);
CheckReferences(typeof(IncrementTool).Assembly, ["SolusAgent.Tools.Api"]);
CheckReferences(typeof(TwoTurnProvider).Assembly, ["SolusAgent.Api", "SolusAgent.Tools.Api", "SolusAgent.Runtime.Api"]);
// A synthetic value and terminal handler prove the optional package's public construction without network access.
using var adapter = new DeepSeekProvider(new("synthetic-unused", 16), new NoNetworkHandler());
_ = RuntimeAgentFactory.Create(new(adapter, [], null, requiredGuarantees: RuntimeGuarantee.None));
Console.WriteLine("Foundation passed: independent public startup and opt-in 4 MiB input, complete 1 MiB artifact/candidate; exact tool and Host effects; single logical-call retry success at 65/128.");

static void CheckReferences(Assembly assembly, string[] allowed)
{
    var references = assembly.GetReferencedAssemblies().Select(reference => reference.Name!).Where(name => name.StartsWith("SolusAgent.", StringComparison.Ordinal));
    if (references.Any(name => !allowed.Contains(name))) throw new InvalidOperationException("Consumer compiled against a forbidden production assembly.");
}

sealed class NoNetworkHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Foundation never dispatches HTTP.");
}
