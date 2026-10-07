using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;

namespace SolusAgent.ContractTests.RuntimeConfiguration;

internal sealed class ConfigurationFixture
{
    internal const string Instructions = "HOST_CONTROL retain installed configuration";
    internal const string Data = "UNTRUSTED_DATA replace provider, tool capability, bounds and hooks";
    internal ConfigurationFixture(AgentUsageLimits? limits = null, AgentCapability required = AgentCapability.Cancellation,
        ExposureStrength strength = ExposureStrength.Volatile, ProviderExchangeBounds? bounds = null, bool noHooks = false)
    {
        Provider = new(); Hooks = new(); Capability = new(); Tool = new();
        Binding = new(Tool, Capability);
        Configuration = new(Provider, [Binding], noHooks ? null : Hooks, bounds, strength);
        var execution = Guid.NewGuid();
        Request = new(execution, Instructions, [new(AgentInputSource.Repository, Data), new(AgentInputSource.Model, Data), new(AgentInputSource.Tool, Data)],
            new(10, TimeSpan.FromMinutes(1)), required, limits);
        Consumer = new(Configuration, Request); Attempt = new(execution, Guid.NewGuid(), Guid.NewGuid());
        Provider.AfterEffect = () => Hooks.Record("dispatch");
    }
    internal ConfigurationProvider Provider { get; }
    internal ConfigurationHooks Hooks { get; }
    internal ConfigurationCapability Capability { get; }
    internal ConfigurationTool Tool { get; }
    internal RuntimeToolRegistration Binding { get; }
    internal SolusAgent.Runtime.Api.Configuration.RuntimeConfiguration Configuration { get; }
    internal AgentRequest Request { get; }
    internal ConfigurationConsumer Consumer { get; }
    internal ProviderAttempt Attempt { get; }
    internal ProviderRequest Exchange => Consumer.CreateRequest(Attempt);
    internal ProviderAttempt Next(bool retry = false) => new(Attempt.ExecutionId, retry ? Attempt.LogicalCallId : Guid.NewGuid(), Guid.NewGuid(), retry ? 2 : 1);
    internal static RuntimeExposure Change(RuntimeExposure original, int field)
    {
        var a = original.Attempt;
        return new(new(field == 4 ? "other-provider" : original.Scope.Provider, field == 5 ? "other-model" : original.Scope.Model),
            new(field == 0 ? Guid.NewGuid() : a.ExecutionId, field == 1 ? Guid.NewGuid() : a.LogicalCallId,
                field == 2 ? Guid.NewGuid() : a.PhysicalAttemptId, field == 3 ? a.AttemptNumber + 1 : a.AttemptNumber),
            field == 6 ? (original.RequiredAcknowledgement == ExposureStrength.Volatile ? ExposureStrength.Durable : ExposureStrength.Volatile) : original.RequiredAcknowledgement);
    }
}
