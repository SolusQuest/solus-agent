using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Execution;

public sealed class SettlementPhaseTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var stop in Enum.GetValues<RuntimeStop>())
        foreach (var invoked in new[] { false, true })
        foreach (var returned in new[] { false, true })
        foreach (var exposure in Enum.GetValues<DispatchExposure>())
        foreach (var usage in new[] { "missing", "zero", "partial" })
            yield return [stop, invoked, returned, exposure, usage];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ClosedPhaseCrossProductDoesNotInventResultsOrErasePendingFacts(RuntimeStop stop, bool invoked, bool returned,
        DispatchExposure dispatch, string usage)
    {
        var attempt = new ProviderAttempt(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var exposure = new RuntimeExposure(new("p", "m"), attempt, ExposureStrength.Volatile);
        var observedUsage = usage == "missing" ? new UsageObservation() : usage == "zero" ? new(0, 0) : new(0, null);
        var observation = new UsageAttemptObservation(attempt.ExecutionId, attempt.LogicalCallId, attempt.PhysicalAttemptId, 1, dispatch, observedUsage);
        RuntimeStop[] delivered = [RuntimeStop.SettlementMissing, RuntimeStop.SettlementFailed, RuntimeStop.SettlementUnknown,
            RuntimeStop.SettlementMismatch, RuntimeStop.HostStopped];
        var localCut = stop is RuntimeStop.Cancelled or RuntimeStop.DurationLimit;
        var valid = !delivered.Contains(stop) && (invoked
            ? (returned ? stop == RuntimeStop.None || localCut : localCut)
            : !returned && stop != RuntimeStop.None && dispatch == DispatchExposure.NotDispatched && usage == "missing");
        RuntimeSettlement Construct() => new(exposure, observation, stop, invoked,
            returned ? ProviderOutcome.Succeeded : null, returned ? ProviderError.None : null);
        if (valid)
        {
            var settlement = Construct(); Assert.Same(observation, settlement.Observation);
            Assert.Equal(invoked, settlement.ProviderInvoked); Assert.Equal(returned, settlement.ProviderOutcome.HasValue);
        }
        else Assert.Throws<ArgumentException>(Construct);
    }

    [Fact]
    public void SealIsIdempotentAndOwnsAStableSnapshotWhileConcurrentLateReportsReject()
    {
        var request = new ProviderRequest(new("p", "m"), new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), []);
        var channel = request.Observation;
        channel.ObserveDispatch(DispatchExposure.Dispatched); channel.CaptureUsage(new(3, 2));
        var snapshot = channel.Seal();
        Parallel.For(0, 16, _ => Assert.Equal(ProviderError.ObservationClosed,
            Assert.Throws<ProviderContractException>(() => channel.CaptureUsage(new(4, 3))).Error));
        Assert.Same(snapshot, channel.Seal()); Assert.Same(snapshot, channel.Snapshot()); Assert.Equal(3, snapshot.Usage.InputTokens);
    }
}
