using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Configuration;
using Xunit;

namespace SolusAgent.ContractTests.Usage;

public sealed class ToolUsageValueTests
{
    [Fact]
    public void AllowanceAndCapabilityValidateIndependentlyOfOtherDimensions()
    {
        Assert.Null(new AgentUsageLimits().MaximumToolInvocations);
        Assert.Equal(int.MaxValue, new AgentUsageLimits(maximumToolInvocations: int.MaxValue).MaximumToolInvocations);
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentUsageLimits(maximumToolInvocations: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentUsageLimits(maximumToolInvocations: -1));
        var request = new AgentRequest(Guid.NewGuid(), "task", [], new(1, TimeSpan.FromSeconds(1)), AgentCapability.ToolInvocationLimit);
        Assert.Null(request.UsageLimits);
        Assert.Equal(AgentCapability.ToolInvocationLimit, new RuntimeSupport(request.RequiredCapabilities, RuntimeGuarantee.None).SupportedCapabilities);
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentRequest(Guid.NewGuid(), "task", [], new(1, TimeSpan.FromSeconds(1)), (AgentCapability)1024));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeSupport((AgentCapability)1024, RuntimeGuarantee.None));
    }

    [Fact]
    public void ToolKnowledgeIsIndependentOfProviderInventoryAndPublicCarriersRetainIt()
    {
        var id = Guid.NewGuid(); var tools = new ToolInvocationUsage(1, 0, 1);
        Assert.Null(new AgentRunUsage(id, UsageInventoryCoverage.Complete, []).ToolInvocations);
        var usage = new AgentRunUsage(id, UsageInventoryCoverage.Unavailable, [], tools);
        var outcome = new AgentOutcome(id, AgentTerminationReason.Cancelled, 1, usage: usage);
        var progress = new AgentProgress(id, 1, usage);
        Assert.Same(tools, outcome.Usage!.ToolInvocations); Assert.Same(tools, progress.Usage!.ToolInvocations);
        Assert.Empty(usage.Attempts); Assert.Equal(UsageInventoryCoverage.Unavailable, usage.Coverage);
        Assert.Equal(1, tools.Invoked); Assert.Equal(0, tools.ReservedUnstarted); Assert.Equal(1, tools.ReleasedUnstarted);
        Assert.All(typeof(ToolInvocationUsage).GetProperties(), property => Assert.False(property.CanWrite));
        Assert.Equal(int.MaxValue, new ToolInvocationUsage(int.MaxValue).Invoked);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolInvocationUsage(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolInvocationUsage(reservedUnstarted: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolInvocationUsage(releasedUnstarted: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolInvocationUsage(int.MaxValue, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolInvocationUsage(0, int.MaxValue, 1));
    }
}
