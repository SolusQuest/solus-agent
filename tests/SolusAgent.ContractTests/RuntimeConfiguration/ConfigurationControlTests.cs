using System.Text;
using System.Text.Json;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.RuntimeConfiguration;

public sealed class ConfigurationControlTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BothBaseAndInterfaceOnlyToolsUseTheSameLiveNarrowBinding(bool interfaceOnly)
    {
        var f = new ConfigurationFixture(); IFunctionTool tool = interfaceOnly ? new ForwardingConfigurationTool(f.Tool) : f.Tool;
        var binding = new RuntimeToolRegistration(tool, f.Capability);
        var configuration = new SolusAgent.Runtime.Api.Configuration.RuntimeConfiguration(f.Provider, [binding], f.Hooks);
        var consumer = new ConfigurationConsumer(configuration, f.Request);
        var provider = await consumer.RunAsync(consumer.CreateRequest(f.Attempt));
        Assert.Equal(ProviderOutcome.Succeeded, provider.Provider!.Outcome);
        var call = new ToolCall("opaque-call", binding.Descriptor.Name, JsonSerializer.Serialize(new { text = ConfigurationFixture.Data }));
        var preparation = binding.Tool.Prepare(call); Assert.True(preparation.Accepted); Assert.Equal(0, f.Capability.Effects);
        var result = await binding.Tool.InvokeAsync(preparation.Prepared!, call, binding.Capability);
        Assert.Equal(ToolOutcome.Succeeded, result.Outcome); Assert.Equal(1, f.Capability.Effects);
        Assert.Equal(ToolError.AlreadyInvoked, (await binding.Tool.InvokeAsync(preparation.Prepared!, call, binding.Capability)).Error);
        Assert.Equal(1, f.Capability.Effects);
    }

    [Fact]
    public async Task MatchingMetadataDoesNotReplaceConcreteCapabilityOrPreparedOwnerGuard()
    {
        var f = new ConfigurationFixture(); var wrong = new SameIdCapability();
        var binding = new RuntimeToolRegistration(new ForwardingConfigurationTool(f.Tool), wrong);
        var call = new ToolCall("call", f.Tool.Descriptor.Name, "{\"text\":\"data\"}");
        var prepared = binding.Tool.Prepare(call).Prepared!;
        Assert.Equal(ToolError.UnsupportedCapability, (await binding.Tool.InvokeAsync(prepared, call, binding.Capability)).Error);
        Assert.Equal(0, wrong.Effects); Assert.Equal(0, f.Capability.Effects);
        var other = new ConfigurationTool();
        Assert.Equal(ToolError.PreparedMismatch, (await other.InvokeAsync(prepared, call, f.Capability)).Error);
        Assert.Equal(ToolError.CallMismatch, (await f.Tool.InvokeAsync(prepared, new("different-call", call.ToolName, call.ArgumentsJson), f.Capability)).Error);
        Assert.Equal(0, f.Capability.Effects);
        Assert.Equal(ToolOutcome.Succeeded, (await f.Tool.InvokeAsync(prepared, call, f.Capability)).Outcome);
    }

    [Theory]
    [InlineData(AgentCapability.WorkUnitLimit)] [InlineData(AgentCapability.DurationLimit)] [InlineData(AgentCapability.UsageThresholds)]
    [InlineData(AgentCapability.ToolInvocationLimit)]
    public async Task ConsumerDisclosesUnsupportedRunGuaranteesAndRejectsBeforeHostWork(AgentCapability required)
    {
        var f = new ConfigurationFixture(required: required);
        Assert.Equal(AgentCapability.None, ConfigurationConsumer.Support.SupportedCapabilities & required);
        var result = await f.Consumer.RunAsync(f.Exchange);
        Assert.Equal(RuntimeStop.UnsupportedCapability, result.AdmissionStop); Assert.Empty(f.Hooks.Events); Assert.Equal(0, f.Provider.Effects);
        Assert.DoesNotContain(typeof(SolusAgent.Runtime.Api.Configuration.RuntimeConfiguration).GetProperties(), property =>
            property.PropertyType == typeof(AgentExecutionBounds) || property.PropertyType == typeof(AgentUsageLimits) || property.PropertyType == typeof(AgentCapability));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task ExistingNullableAndPartialRunLimitsRemainAuthoritative(int mode)
    {
        var limits = mode switch { 0 => null, 1 => new AgentUsageLimits(maximumLogicalCalls: 1), 2 => new AgentUsageLimits(maximumPhysicalDispatches: 2), _ => new AgentUsageLimits(inputTokenThreshold: 1) };
        var f = new ConfigurationFixture(limits);
        Assert.Same(limits, f.Consumer.Request.UsageLimits); Assert.Same(f.Request.Bounds, f.Consumer.Request.Bounds);
        await f.Consumer.RunAsync(f.Exchange);
        var second = await f.Consumer.RunAsync(f.Consumer.CreateRequest(f.Next()));
        Assert.Equal(mode == 1 ? RuntimeStop.ResourceLimit : RuntimeStop.None, second.AdmissionStop);
        Assert.Equal(mode == 1 ? 1 : 2, f.Provider.Effects);
        if (mode == 2)
        {
            Assert.Equal(RuntimeStop.ResourceLimit, (await f.Consumer.RunAsync(f.Consumer.CreateRequest(f.Next()))).AdmissionStop);
            Assert.Equal(2, f.Provider.Effects);
        }
        if (mode == 3) Assert.Equal(AgentCapability.None, ConfigurationConsumer.Support.SupportedCapabilities & AgentCapability.UsageThresholds);
    }

    [Fact]
    public async Task PhysicalLimitIncludesNewRetryAdmissionAndDoesNotInventAnotherDispatch()
    {
        var f = new ConfigurationFixture(new(maximumPhysicalDispatches: 1));
        f.Provider.Response = _ => throw new InvalidOperationException("synthetic failure");
        await f.Consumer.RunAsync(f.Exchange);
        Assert.Equal(RuntimeStop.ResourceLimit, (await f.Consumer.RunAsync(f.Consumer.CreateRequest(f.Next(retry: true)))).AdmissionStop);
        Assert.Equal(1, f.Provider.Effects); Assert.Equal(1, f.Hooks.Events.Count(x => x == "expose"));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task InvalidAttemptInventoryCannotReachNewHooksOrEffects(int field)
    {
        var f = new ConfigurationFixture(); await f.Consumer.RunAsync(f.Exchange);
        var a = f.Attempt;
        var next = field switch
        {
            0 => new ProviderAttempt(a.ExecutionId, Guid.NewGuid(), a.PhysicalAttemptId),
            1 => new ProviderAttempt(a.ExecutionId, a.LogicalCallId, Guid.NewGuid(), 1),
            2 => new ProviderAttempt(a.ExecutionId, a.LogicalCallId, Guid.NewGuid(), 3),
            _ => new ProviderAttempt(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
        };
        Assert.Equal(RuntimeStop.InvalidAssociation, (await f.Consumer.RunAsync(f.Consumer.CreateRequest(next))).AdmissionStop);
        Assert.Equal(1, f.Provider.Effects); Assert.Equal(1, f.Hooks.Events.Count(x => x == "expose"));
    }

    [Fact]
    public async Task LiveConfigurationCannotBeSelectedByModelToolOrSavedDataAndCanariesStayRestricted()
    {
        var f = new ConfigurationFixture();
        var saved = ConfigurationConsumer.SaveSyntheticData(ConfigurationFixture.Data);
        Assert.Contains(ConfigurationFixture.Data, Encoding.UTF8.GetString(saved.CopyRestrictedPayload()));
        var exchange = f.Exchange;
        Assert.Equal(ConfigurationFixture.Instructions, exchange.Inputs[0].Text);
        Assert.Equal(ProviderInputKind.HostInstruction, exchange.Inputs[0].Kind);
        Assert.All(exchange.Inputs.Skip(1), input => Assert.Equal(ProviderInputKind.InputData, input.Kind));
        var result = await f.Consumer.RunAsync(exchange);
        Assert.Same(f.Provider, f.Configuration.Provider); Assert.Same(f.Capability, f.Configuration.Tools[0].Capability);
        Assert.Same(f.Hooks, f.Configuration.Hooks); Assert.Equal(ConfigurationFixture.Instructions, f.Consumer.Request.Instructions);
        // Inspect actual model-visible values; the provider contract is not a wire serialization DTO.
        var modelVisible = string.Join('\n', exchange.Inputs.Select(input => input.Text).Concat(exchange.Tools.SelectMany(tool =>
            new[] { tool.Name, tool.Description, tool.CapabilityId, tool.InputSchema.NormalizedJson, tool.ResultSchema.NormalizedJson }))
            .Prepend(exchange.Scope.Model).Prepend(exchange.Scope.Provider));
        var ordinary = JsonSerializer.Serialize(result.Diagnostic) + JsonSerializer.Serialize(f.Configuration.Describe()) + JsonSerializer.Serialize(saved)
            + result + f.Configuration + f.Binding + result.Settlement;
        foreach (var canary in new[] { ConfigurationProvider.CredentialCanary, ConfigurationCapability.CredentialCanary, ConfigurationHooks.CredentialCanary })
        {
            Assert.DoesNotContain(canary, modelVisible); Assert.DoesNotContain(canary, ordinary);
            Assert.DoesNotContain(canary, Encoding.UTF8.GetString(saved.CopyRestrictedPayload()));
        }
        Assert.DoesNotContain(ConfigurationFixture.Data, ordinary); Assert.DoesNotContain(ConfigurationProvider.ModelCanary, ordinary);
        Assert.DoesNotContain(f.Configuration.Scope.Model, ordinary);
        Assert.Contains(typeof(ConfigurationConsumer).Assembly.GetReferencedAssemblies(), reference => reference.Name == "SolusAgent.Runtime.Api");
        Assert.DoesNotContain(typeof(ConfigurationConsumer).Assembly.GetReferencedAssemblies(), reference => reference.Name == "SolusAgent.Runtime");
    }

    [Fact]
    public async Task RequiredHookAbsenceAndForeignProviderScopeAreRejectedBeforeEffects()
    {
        var missing = new ConfigurationFixture(noHooks: true);
        Assert.Equal(RuntimeStop.MissingHooks, (await missing.Consumer.RunAsync(missing.Exchange)).AdmissionStop); Assert.Equal(0, missing.Provider.Effects);
        var f = new ConfigurationFixture();
        var foreign = new ProviderRequest(new("other", f.Configuration.Scope.Model), f.Attempt, [], [f.Binding.Descriptor]);
        Assert.Equal(RuntimeStop.InvalidAssociation, (await f.Consumer.RunAsync(foreign)).AdmissionStop); Assert.Empty(f.Hooks.Events);
    }

    private sealed class SameIdCapability : IToolCapability
    {
        public string CapabilityId => "configuration_echo";
        public int Effects => 0;
    }
}
