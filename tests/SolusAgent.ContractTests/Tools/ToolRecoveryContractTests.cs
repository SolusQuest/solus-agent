using CustomTools;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Tools;

public sealed class ToolRecoveryContractTests
{
    [Fact]
    public void BindingAdmissionNeedsNoPreparedCallOperationOrQuery()
    {
        IRecoverableFunctionTool tool = new RecoverableCounterTool();
        var cap = new RecoveryCounterCapability("target", ToolRecoveryGuarantee.QueryOutcome);
        var binding = new ToolRecoveryBinding("keyed_recovery", "target", cap.Guarantee);
        Assert.Equal(ToolError.None, tool.ValidateCapability(cap));
        Assert.Equal(ToolError.None, tool.ValidateRecoveryBinding(binding, cap));
        Assert.Equal(ToolError.UnsupportedCapability, tool.ValidateRecoveryBinding(new("keyed_recovery", "other", cap.Guarantee), cap));
        Assert.Equal(ToolError.UnsupportedCapability, tool.ValidateRecoveryBinding(new("keyed_recovery", "target", ToolRecoveryGuarantee.ConcurrentSafeRepeat), cap));
        Assert.Equal(ToolError.UnsupportedCapability, tool.ValidateRecoveryBinding(binding, new CounterCapability()));
        cap.Allowed = false;
        Assert.Equal(ToolError.UnsupportedCapability, tool.ValidateCapability(cap));
        Assert.Equal(ToolError.UnsupportedCapability, tool.ValidateRecoveryBinding(binding, cap));
        Assert.Equal(0, cap.Invocations); Assert.Equal(0, cap.Queries); Assert.Equal(0, cap.Effects);
    }

    [Theory]
    [InlineData("5.00")] [InlineData("5e0")] [InlineData("0.5e1")]
    public async Task ActualKeyedProducerReadsExactIntegralSpellingWithoutChangingOperationOrReceipt(string token)
    {
        var tool = new RecoverableCounterTool(); var cap = new RecoveryCounterCapability("target", ToolRecoveryGuarantee.QueryOutcome);
        var call = new ToolCall("exact", "recover", "{\"amount\":" + token + "}");
        var operation = new ToolOperation(Guid.NewGuid(), call, new("keyed_recovery", "target", cap.Guarantee));
        var prepared = tool.Prepare(call); Assert.True(prepared.Accepted); Assert.Equal(0, cap.Invocations);
        var actual = await tool.InvokeOperationAsync(prepared.Prepared!, operation, cap);
        Assert.Equal(ToolOutcome.Succeeded, actual.Outcome); Assert.Equal("{\"total\":5}", actual.Json);
        var receipt = await tool.QueryOutcomeAsync(operation, cap);
        Assert.Equal(call.ArgumentsJson, receipt.RecordedCall!.ArgumentsJson); Assert.Equal(actual.Json, receipt.Json);
        var historical = ToolResult.RestoreHistorical(tool.Descriptor, call, receipt.RecordedCall, receipt.Outcome!.Value,
            receipt.Error!.Value, receipt.InvocationStarted!.Value, receipt.Json);
        Assert.True(historical.IsHistorical); Assert.Equal(1, cap.Invocations); Assert.Equal(1, cap.Effects);
    }

    [Fact]
    public void OriginalIdentityAndBindingAreBoundedAndDiagnosticsRestricted()
    {
        var call = new ToolCall("CALL_CANARY", "recover", "{\"amount\":5}");
        var binding = new ToolRecoveryBinding("keyed_recovery", "TARGET_CANARY", ToolRecoveryGuarantee.QueryOutcome);
        var operation = new ToolOperation(Guid.NewGuid(), call, binding);
        Assert.True(operation.Matches(new(operation.OperationId, call, new("keyed_recovery", "TARGET_CANARY", binding.Guarantee))));
        Assert.False(operation.Matches(new(Guid.NewGuid(), call, binding)));
        Assert.False(operation.Matches(new(operation.OperationId, call, new("keyed_recovery", "CHANGED", binding.Guarantee))));
        Assert.False(operation.Matches(new(operation.OperationId, new("CALL_CANARY", "recover", "{\"amount\":6}"), binding)));
        Assert.Throws<ArgumentException>(() => new ToolOperation(Guid.Empty, call));
        Assert.Throws<ToolContractException>(() => new ToolRecoveryBinding("bad-id", "target", binding.Guarantee));
        Assert.Throws<ToolContractException>(() => new ToolRecoveryBinding("valid", new string('x', 129), binding.Guarantee));
        Assert.Throws<ToolContractException>(() => new ToolRecoveryBinding("valid", "\ud800", binding.Guarantee));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolRecoveryBinding("valid", "target", (ToolRecoveryGuarantee)99));
        Assert.Equal(nameof(ToolOperation), operation.ToString()); Assert.Equal(nameof(ToolRecoveryBinding), binding.ToString());
        var receipt = ToolRecoveryReceipt.Found(operation, call, ToolOutcome.Succeeded, ToolError.None, true, "{\"total\":5}");
        Assert.Equal(nameof(ToolRecoveryReceipt), receipt.ToString());
        var historical = ToolResult.RestoreHistorical(new RecoverableCounterTool().Descriptor, call, receipt.RecordedCall!, receipt.Outcome!.Value,
            receipt.Error!.Value, receipt.InvocationStarted!.Value, receipt.Json);
        Assert.True(historical.IsHistorical); Assert.True(historical.InvocationStarted);
        Assert.Null(ToolRecoveryReceipt.Unresolved(operation).RecordedCall);
        Assert.Throws<ToolContractException>(() => ToolRecoveryReceipt.Found(operation, call, ToolOutcome.Succeeded, ToolError.None, true, new string('x', ToolLimits.PayloadBytes + 1)));
    }
    [Fact]
    public async Task OperationAwareProducerPreservesConcurrentSingleUseAndFreshKeyedAttempt()
    {
        var tool = new RecoverableCounterTool(); var capability = new RecoveryCounterCapability("target", ToolRecoveryGuarantee.ConcurrentSafeRepeat);
        var call = new ToolCall("once", "recover", "{\"amount\":5}");
        var operation = new ToolOperation(Guid.NewGuid(), call, new("keyed_recovery", "target", capability.Guarantee));
        var prepared = tool.Prepare(call).Prepared!;
        var both = await Task.WhenAll(Task.Run(async () => await tool.InvokeOperationAsync(prepared, operation, capability)),
            Task.Run(async () => await tool.InvokeOperationAsync(prepared, operation, capability)));
        Assert.Single(both, r => r.Outcome == ToolOutcome.Succeeded); Assert.Single(both, r => r.Error == ToolError.AlreadyInvoked);
        Assert.Equal(1, capability.Invocations); Assert.Equal(1, capability.Effects);
        var repeated = await tool.InvokeOperationAsync(tool.Prepare(call).Prepared!, operation, capability);
        Assert.Equal(ToolOutcome.Succeeded, repeated.Outcome); Assert.Equal(2, capability.Invocations); Assert.Equal(1, capability.Effects);
        var foreign = new RecoverableCounterTool();
        Assert.Equal(ToolError.PreparedMismatch, (await foreign.InvokeOperationAsync(prepared, operation, capability)).Error);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.False((await tool.InvokeOperationAsync(tool.Prepare(call).Prepared!, operation, capability, cancelled.Token)).InvocationStarted);
        Assert.Equal(2, capability.Invocations);
        Assert.Throws<ArgumentException>(() => new RuntimeToolRegistration(new CounterTool(), new CounterCapability(), operation.Recovery));
    }
    [Fact]
    public async Task QueryOnlyGuaranteeNeverExecutesAnOriginalOperationAsFallback()
    {
        var tool = new RecoverableCounterTool(); var cap = new RecoveryCounterCapability("target", ToolRecoveryGuarantee.QueryOutcome);
        var operation = new ToolOperation(Guid.NewGuid(), new("unknown", "recover", "{\"amount\":5}"), new("keyed_recovery", "target", cap.Guarantee));
        var queried = await tool.QueryOutcomeAsync(operation, cap);
        Assert.Null(queried.RecordedCall); Assert.Equal(1, cap.Queries); Assert.Equal(0, cap.Invocations); Assert.Equal(0, cap.Effects);
        cap.Allowed = false;
        Assert.Equal(ToolError.UnsupportedCapability, tool.ValidateOperation(operation, cap));
        await Assert.ThrowsAsync<ToolContractException>(async () => await tool.QueryOutcomeAsync(operation, cap));
        Assert.Equal(1, cap.Queries);
    }
}
