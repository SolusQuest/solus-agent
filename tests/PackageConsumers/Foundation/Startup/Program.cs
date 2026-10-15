using System.Reflection;
using PackageConsumers.Foundation.Business;
using PackageConsumers.Foundation.CustomProvider;
using PackageConsumers.Foundation.CustomTool;
using SolusAgent.Providers.DeepSeek;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Startup;

var provider = new TwoTurnProvider();
var counter = new Counter();
var hooks = new ExposureHooks();
var agent = RuntimeAgentFactory.Create(new(provider, [new(new IncrementTool(), counter)], hooks));
var outcome = await BusinessWorkflow.ExecuteAsync(agent);
if (counter.Effects != 1 || provider.Calls != 2 || hooks.Permissions != 2 || hooks.Closures != 2
    || outcome.Usage!.Attempts.Any(attempt => attempt.Usage.InputTokens != 3 || attempt.Usage.OutputTokens != 2))
    throw new InvalidOperationException("Public startup composition did not execute the expected effects and observations.");

CheckReferences(typeof(BusinessWorkflow).Assembly, ["SolusAgent.Api"]);
CheckReferences(typeof(IncrementTool).Assembly, ["SolusAgent.Tools.Api"]);
CheckReferences(typeof(TwoTurnProvider).Assembly, ["SolusAgent.Api", "SolusAgent.Tools.Api", "SolusAgent.Runtime.Api"]);
// A synthetic value and terminal handler prove the optional package's public construction without network access.
using var adapter = new DeepSeekProvider(new("synthetic-unused", 16), new NoNetworkHandler());
_ = RuntimeAgentFactory.Create(new(adapter, [], null, requiredGuarantees: RuntimeGuarantee.None));
Console.WriteLine("Foundation passed: public startup, Api business, Tools.Api tool, Runtime.Api provider; two provider turns and one tool effect.");

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
