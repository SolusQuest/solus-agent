using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ConsumerProbes.CustomProvider;
using Xunit;

namespace SolusAgent.ContractTests.Usage;

public sealed class AccountingContractTests
{
    [Fact]
    public void MissingAndIncompatibleHostPolicyIsRejectedAtConstruction()
    {
        Assert.Throws<ArgumentNullException>(() => new AgentAccountingPolicy(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentAccountingPolicy(new(1, 1), inputAllowance: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentAccountingPolicy(new(1, 1), outputAllowance: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentAccountingPolicy(new(1, 1), unknownUsage: (UnknownUsagePolicy)9));
        foreach (var mode in new[] { UnknownUsagePolicy.ConservativeCharge, UnknownUsagePolicy.ContinueUnknown })
        {
            var policy = new AgentAccountingPolicy(new(0, 0), unknownUsage: mode);
            Assert.Throws<ArgumentException>(() => new AgentUsageLimits(accountingPolicy: policy));
            foreach (var output in new[] { false, true })
            {
                var limits = new AgentUsageLimits(maximumPhysicalDispatches: 2, inputTokenThreshold: output ? null : 1,
                    outputTokenThreshold: output ? 1 : null, accountingPolicy: policy);
                Assert.Throws<ArgumentException>(() => Request(AgentCapability.UsageThresholds, limits));
                Assert.Same(policy, Request(AgentCapability.UsageAccounting, limits).UsageLimits!.AccountingPolicy);
            }
        }
        Assert.Throws<ArgumentException>(() => Request(AgentCapability.UsageAccounting, null));
        Assert.False(ConfigurationConsumer.Support.SupportedCapabilities.HasFlag(AgentCapability.UsageAccounting));
    }
    private static AgentRequest Request(AgentCapability required, AgentUsageLimits? limits) =>
        new(Guid.NewGuid(), "i", [], new(1, TimeSpan.FromSeconds(1)), required, limits);

    [Fact]
    public void ImmutableSnapshotRejectsWrongInventoryBasisAndObservation()
    {
        var id = Guid.NewGuid(); var call = Guid.NewGuid(); var physical = Guid.NewGuid();
        var policy = new AgentAccountingPolicy(new(8, 5), 20, 20);
        var entry = new AttemptAccounting(id, call, physical, 1, policy.Reservation,
            new(AccountingDisposition.Measured, 3), new(AccountingDisposition.Unresolved, 5));
        var entries = new List<AttemptAccounting> { entry }; var snapshot = new RunAccountingSnapshot(id, policy, entries); entries.Clear();
        Assert.Single(snapshot.Attempts); Assert.True(snapshot.Matches(AccountingHostProbe.Copy(snapshot)!));
        Assert.Throws<ArgumentException>(() => new RunAccountingSnapshot(Guid.NewGuid(), policy, [entry]));
        Assert.Throws<ArgumentException>(() => new RunAccountingSnapshot(id, policy, [entry, entry]));
        Assert.Throws<ArgumentException>(() => new RunAccountingSnapshot(id, new(new(9, 5)), [entry]));
        Assert.Throws<ArgumentException>(() => new RunAccountingSnapshot(id, new(new(8, 5), unknownUsage: UnknownUsagePolicy.ConservativeCharge), [entry]));
        Assert.Throws<ArgumentException>(() => new AccountingDimension(AccountingDisposition.Released, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AccountingDimension((AccountingDisposition)9, 1));
        Assert.Throws<ArgumentException>(() => new AttemptAccounting(id, call, physical, 1, policy.Reservation,
            new(AccountingDisposition.Reserved, 8), new(AccountingDisposition.Measured, 1)));
        Assert.Throws<ArgumentException>(() => new AttemptAccounting(id, call, physical, 1, policy.Reservation,
            new(AccountingDisposition.Measured, 3), new(AccountingDisposition.Unresolved, 4)));
        var observation = new UsageAttemptObservation(id, call, physical, 1, DispatchExposure.Dispatched, new(3, null));
        Assert.Same(snapshot, new AgentRunUsage(id, UsageInventoryCoverage.Complete, [observation], accounting: snapshot).Accounting);
        Assert.Throws<ArgumentException>(() => new AgentRunUsage(id, UsageInventoryCoverage.Partial, [observation], accounting: snapshot));
        Assert.Throws<ArgumentException>(() => new AgentRunUsage(id, UsageInventoryCoverage.Complete, [], accounting: snapshot));
        Assert.Throws<ArgumentException>(() => new AgentRunUsage(id, UsageInventoryCoverage.Complete,
            [new(id, call, physical, 1, DispatchExposure.Dispatched, new(4, null))], accounting: snapshot));
        Assert.Equal(AccountingBalanceCoverage.Known, snapshot.Input.Coverage);
        Assert.Equal(AccountingBalanceCoverage.Unknown, snapshot.Output.Coverage);
        Assert.Equal(15, snapshot.Output.RemainingAllowance);
    }

    [Theory]
    [InlineData(UnknownUsagePolicy.ConservativeCharge, AccountingDisposition.ConservativeCharge, AccountingBalanceCoverage.Provisional)]
    [InlineData(UnknownUsagePolicy.ContinueUnknown, AccountingDisposition.Unresolved, AccountingBalanceCoverage.Unknown)]
    public void ExplicitZeroEstimateDoesNotClaimKnownConsumption(UnknownUsagePolicy mode, AccountingDisposition disposition, AccountingBalanceCoverage coverage)
    {
        var id = Guid.NewGuid(); var policy = new AgentAccountingPolicy(new(0, 0), 10, 10, mode);
        var snapshot = new RunAccountingSnapshot(id, policy, [new(id, Guid.NewGuid(), Guid.NewGuid(), 1, policy.Reservation,
            new(disposition, 0), new(disposition, 0))]);
        Assert.Equal(0, snapshot.Input.AccountedTokens); Assert.Equal(10, snapshot.Input.RemainingAllowance);
        Assert.Equal(coverage, snapshot.Input.Coverage); Assert.Equal(coverage, snapshot.Output.Coverage);
    }
}
