using System.Collections;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Usage;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using Xunit;
using Configuration = SolusAgent.Runtime.Api.Configuration.RuntimeConfiguration;

namespace SolusAgent.ContractTests.RuntimeConfiguration;

public sealed class ConfigurationBoundaryTests
{
    [Fact]
    public void RegistryCopiesExactlyTheCountAdmittedOnceBeforeAllocation()
    {
        var f = new ConfigurationFixture();
        var bindings = Enumerable.Range(0, ProviderLimits.Tools).Select(i => new RuntimeToolRegistration(new ConfigurationTool("echo_" + i), f.Capability)).ToArray();
        var source = new ChangingRegistry(bindings, ProviderLimits.Tools, ProviderLimits.Tools + 1);
        var configuration = new Configuration(f.Provider, source, f.Hooks);
        Assert.Equal(ProviderLimits.Tools, configuration.Tools.Count); Assert.Equal(1, source.CountReads); Assert.Equal(ProviderLimits.Tools, source.IndexReads);
        var original = configuration.Tools[0]; bindings[0] = f.Binding;
        Assert.Same(original, configuration.Tools[0]); Assert.Equal("echo_0", configuration.Tools[0].Descriptor.Name);
    }

    [Theory]
    [InlineData(-1, ProviderError.InvalidInput)]
    [InlineData(17, ProviderError.LimitExceeded)]
    [InlineData(int.MaxValue, ProviderError.LimitExceeded)]
    public void InvalidInitialRegistryCountRejectsBeforeIndexing(int count, ProviderError error)
    {
        var f = new ConfigurationFixture(); var source = new ChangingRegistry([f.Binding], count, 1);
        Assert.Equal(error, Assert.Throws<ProviderContractException>(() => new Configuration(f.Provider, source, f.Hooks)).Error);
        Assert.Equal(1, source.CountReads); Assert.Equal(0, source.IndexReads); Assert.Equal(0, f.Provider.Effects);
    }

    [Fact]
    public void LowerConfigurationCeilingNullEntriesAndDuplicateNamesReject()
    {
        var f = new ConfigurationFixture(); var other = new RuntimeToolRegistration(new ConfigurationTool("other"), f.Capability);
        var source = new ChangingRegistry([f.Binding, other], 2, 1);
        Assert.Equal(ProviderError.LimitExceeded, Assert.Throws<ProviderContractException>(() => new Configuration(f.Provider, source, f.Hooks, new(maximumTools: 1))).Error);
        Assert.Equal(0, source.IndexReads);
        Assert.Equal(ProviderError.InvalidInput, Assert.Throws<ProviderContractException>(() => new Configuration(f.Provider, [null!], f.Hooks)).Error);
        Assert.Equal(ProviderError.InvalidAssociation, Assert.Throws<ProviderContractException>(() => new Configuration(f.Provider,
            [f.Binding, new(new ConfigurationTool(), f.Capability)], f.Hooks)).Error);
        Assert.Single(new Configuration(f.Provider, [f.Binding], f.Hooks, new(maximumTools: 1)).Tools);
    }

    [Fact]
    public void BindingGetterFailureAndMismatchedMetadataDoNotLeakPrivateException()
    {
        var f = new ConfigurationFixture();
        var failure = Assert.Throws<ArgumentException>(() => new RuntimeToolRegistration(f.Tool, new ThrowingCapability()));
        Assert.DoesNotContain(ConfigurationCapability.CredentialCanary, failure.ToString()); Assert.Null(failure.InnerException);
        Assert.Throws<ArgumentException>(() => new RuntimeToolRegistration(f.Tool, new ProbeCapability()));
        Assert.Throws<ArgumentNullException>(() => new RuntimeToolRegistration(f.Tool, null!));
        Assert.Throws<ArgumentNullException>(() => new RuntimeToolRegistration(null!, f.Capability));
    }

    [Fact]
    public async Task FiniteProviderInputsAreAdmittedBeforeToyProjectionAndLooserRequestIsRejected()
    {
        var f = new ConfigurationFixture(bounds: new(maximumInputs: 3));
        Assert.Equal(ProviderError.LimitExceeded, Assert.Throws<ProviderContractException>(() => f.Consumer.CreateRequest(f.Attempt)).Error);
        var looser = new ProviderRequest(f.Configuration.Scope, f.Attempt, [], [f.Binding.Descriptor]);
        Assert.Equal(RuntimeStop.InvalidAssociation, (await f.Consumer.RunAsync(looser)).AdmissionStop);
        Assert.Empty(f.Hooks.Events); Assert.Equal(0, f.Provider.Effects);
        var exact = new ConfigurationFixture(bounds: new(maximumInputs: 4));
        Assert.Equal(RuntimeStop.None, (await exact.Consumer.RunAsync(exact.Exchange)).AdmissionStop);
        Assert.Throws<ProviderContractException>(() => ConfigurationConsumer.SaveSyntheticData(new string('x', ProviderLimits.TextBytes + 1)));
        Assert.Throws<ProviderContractException>(() => ConfigurationConsumer.SaveSyntheticData("\ud800"));
    }

    [Fact]
    public async Task ToyCapacityIsExplicitAndDoesNotCreateMandatoryOuterUsagePolicy()
    {
        var f = new ConfigurationFixture(); Assert.Null(f.Request.UsageLimits);
        for (var i = 0; i < ConfigurationConsumer.ScriptCapacity; i++)
        {
            var attempt = i == 0 ? f.Attempt : f.Next();
            Assert.Equal(RuntimeStop.None, (await f.Consumer.RunAsync(f.Consumer.CreateRequest(attempt))).AdmissionStop);
        }
        Assert.Equal(RuntimeStop.ResourceLimit, (await f.Consumer.RunAsync(f.Consumer.CreateRequest(f.Next()))).AdmissionStop);
        Assert.Equal(ConfigurationConsumer.ScriptCapacity, f.Provider.Effects);
    }

    [Fact]
    public void UndefinedSupportAndAcknowledgementConfigurationCannotClaimGuarantees()
    {
        var f = new ConfigurationFixture();
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeSupport((AgentCapability)1024, RuntimeGuarantee.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeSupport(AgentCapability.None, (RuntimeGuarantee)4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Configuration(f.Provider, [], f.Hooks, requiredAcknowledgement: (ExposureStrength)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Configuration(f.Provider, [], f.Hooks, requiredGuarantees: (RuntimeGuarantee)4));
        Assert.Equal(RuntimeStop.UnsupportedCapability, f.Configuration.CheckSupport(new(AgentCapability.None, RuntimeGuarantee.None)));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void IncoherentExposureReceiptsRejectBeforeAnyDecisionCanBeUsed(int kind)
    {
        var f = new ConfigurationFixture(); var exposure = new RuntimeExposure(f.Configuration.Scope, f.Attempt, ExposureStrength.Volatile);
        Assert.ThrowsAny<ArgumentException>(() => kind switch
        {
            0 => new ExposureAcknowledgement(exposure, RuntimeHookStatus.Acknowledged),
            1 => new ExposureAcknowledgement(exposure, RuntimeHookStatus.Acknowledged, ExposureDecision.Permit),
            2 => new ExposureAcknowledgement(exposure, RuntimeHookStatus.Failed, ExposureDecision.Permit, ExposureStrength.Durable),
            3 => new ExposureAcknowledgement(exposure, RuntimeHookStatus.Unknown, strength: ExposureStrength.Durable),
            4 => new ExposureAcknowledgement(exposure, (RuntimeHookStatus)99),
            5 => new ExposureAcknowledgement(exposure, RuntimeHookStatus.Acknowledged, (ExposureDecision)99),
            _ => new ExposureAcknowledgement(exposure, RuntimeHookStatus.Acknowledged, ExposureDecision.Permit, (ExposureStrength)99),
        });
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void SettlementCannotRewriteFullAttemptIdentity(int field)
    {
        var f = new ConfigurationFixture(); var exposure = new RuntimeExposure(f.Configuration.Scope, f.Attempt, ExposureStrength.Volatile);
        var a = ConfigurationFixture.Change(exposure, field).Attempt;
        var wrong = new UsageAttemptObservation(a.ExecutionId, a.LogicalCallId, a.PhysicalAttemptId, a.AttemptNumber, DispatchExposure.NotDispatched, new());
        Assert.Throws<ArgumentException>(() => new RuntimeSettlement(exposure, wrong, RuntimeStop.Cancelled, false));
    }

    [Fact]
    public void SettlementShapeSeparatesNeverInvokedProviderAndHookReceiptFromAccounting()
    {
        var f = new ConfigurationFixture(); var exposure = new RuntimeExposure(f.Configuration.Scope, f.Attempt, ExposureStrength.Durable);
        var observation = new UsageAttemptObservation(f.Attempt.ExecutionId, f.Attempt.LogicalCallId, f.Attempt.PhysicalAttemptId, 1, DispatchExposure.NotDispatched, new());
        Assert.Null(new RuntimeSettlement(exposure, observation, RuntimeStop.Cancelled, false).ProviderOutcome);
        Assert.Throws<ArgumentException>(() => new RuntimeSettlement(exposure, observation, RuntimeStop.None, false));
        Assert.Throws<ArgumentException>(() => new RuntimeSettlement(exposure, observation, RuntimeStop.None, true, ProviderOutcome.Succeeded));
        Assert.Throws<ArgumentException>(() => new RuntimeSettlement(exposure, observation, RuntimeStop.None, true, ProviderOutcome.Failed, ProviderError.None));
        Assert.Throws<ArgumentException>(() => new SettlementAcknowledgement(exposure, RuntimeHookStatus.Acknowledged));
        Assert.Throws<ArgumentException>(() => new SettlementAcknowledgement(exposure, RuntimeHookStatus.Unknown, RuntimeContinuation.Continue));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SettlementAcknowledgement(exposure, RuntimeHookStatus.Acknowledged, (RuntimeContinuation)99));
    }

    public static TheoryData<RuntimeStop, bool, bool> PhaseCases => new()
    {
        { RuntimeStop.None, true, false },
        { RuntimeStop.Cancelled, true, true },
        { RuntimeStop.DurationLimit, true, true },
        { RuntimeStop.UnsupportedCapability, false, true },
        { RuntimeStop.MissingHooks, false, true },
        { RuntimeStop.InvalidAssociation, false, true },
        { RuntimeStop.ResourceLimit, false, true },
        { RuntimeStop.ExposureDenied, false, true },
        { RuntimeStop.ExposureMissing, false, true },
        { RuntimeStop.ExposureFailed, false, true },
        { RuntimeStop.ExposureUnknown, false, true },
        { RuntimeStop.ExposureMismatch, false, true },
        { RuntimeStop.DurableAcknowledgementRequired, false, true },
        { RuntimeStop.SettlementMissing, false, false },
        { RuntimeStop.SettlementFailed, false, false },
        { RuntimeStop.SettlementUnknown, false, false },
        { RuntimeStop.SettlementMismatch, false, false },
        { RuntimeStop.HostStopped, false, false },
    };

    [Theory]
    [MemberData(nameof(PhaseCases))]
    public void SettlementStopCannotContradictProviderInvocation(RuntimeStop stop, bool invokedAllowed, bool noProviderAllowed)
    {
        _ = noProviderAllowed;
        var f = new ConfigurationFixture(); var exposure = new RuntimeExposure(f.Configuration.Scope, f.Attempt, ExposureStrength.Volatile);
        var dispatched = new UsageAttemptObservation(f.Attempt.ExecutionId, f.Attempt.LogicalCallId, f.Attempt.PhysicalAttemptId, 1,
            DispatchExposure.Dispatched, new(1, 1));
        var unknown = new UsageAttemptObservation(f.Attempt.ExecutionId, f.Attempt.LogicalCallId, f.Attempt.PhysicalAttemptId, 1,
            DispatchExposure.Unknown, new());
        if (invokedAllowed)
        {
            Assert.Same(dispatched, new RuntimeSettlement(exposure, dispatched, stop, true, ProviderOutcome.Succeeded, ProviderError.None).Observation);
            Assert.Same(unknown, new RuntimeSettlement(exposure, unknown, stop, true, ProviderOutcome.Failed, ProviderError.ProviderFailed).Observation);
        }
        else
        {
            Assert.Throws<ArgumentException>(() => new RuntimeSettlement(exposure, dispatched, stop, true, ProviderOutcome.Succeeded, ProviderError.None));
            Assert.Throws<ArgumentException>(() => new RuntimeSettlement(exposure, unknown, stop, true, ProviderOutcome.Failed, ProviderError.ProviderFailed));
        }
    }

    [Theory]
    [MemberData(nameof(PhaseCases))]
    public void SettlementStopMustDescribePhaseBeforeItsOwnDelivery(RuntimeStop stop, bool invokedAllowed, bool noProviderAllowed)
    {
        _ = invokedAllowed;
        var f = new ConfigurationFixture(); var exposure = new RuntimeExposure(f.Configuration.Scope, f.Attempt, ExposureStrength.Volatile);
        var observation = new UsageAttemptObservation(f.Attempt.ExecutionId, f.Attempt.LogicalCallId, f.Attempt.PhysicalAttemptId, 1,
            DispatchExposure.NotDispatched, new());
        if (noProviderAllowed)
        {
            var settlement = new RuntimeSettlement(exposure, observation, stop, false);
            Assert.Null(settlement.ProviderOutcome); Assert.Same(observation, settlement.Observation);
        }
        else Assert.Throws<ArgumentException>(() => new RuntimeSettlement(exposure, observation, stop, false));
    }

    [Fact]
    public void NeverInvokedProviderHasNoMeasuredUsage()
    {
        var f = new ConfigurationFixture(); var exposure = new RuntimeExposure(f.Configuration.Scope, f.Attempt, ExposureStrength.Volatile);
        var observation = new UsageAttemptObservation(f.Attempt.ExecutionId, f.Attempt.LogicalCallId, f.Attempt.PhysicalAttemptId, 1,
            DispatchExposure.NotDispatched, new(0, 0));
        Assert.Throws<ArgumentException>(() => new RuntimeSettlement(exposure, observation, RuntimeStop.Cancelled, false));
    }

    private sealed class ThrowingCapability : SolusAgent.Tools.Api.IToolCapability
    { public string CapabilityId => throw new InvalidOperationException(ConfigurationCapability.CredentialCanary); }

    private sealed class ChangingRegistry(RuntimeToolRegistration[] values, int first, int later) : IReadOnlyList<RuntimeToolRegistration>
    {
        public int CountReads { get; private set; }
        public int IndexReads { get; private set; }
        public int Count => ++CountReads == 1 ? first : later;
        public RuntimeToolRegistration this[int index] { get { IndexReads++; return values[index]; } }
        public IEnumerator<RuntimeToolRegistration> GetEnumerator() => throw new InvalidOperationException("The boundary must not enumerate unadmitted input.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
