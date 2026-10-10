using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ContractTests.Runtime.Context;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Providers.DeepSeek;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.Adapter;

internal sealed record PersistenceObservation(int Pid, ContextAdmission Admission, ContextRejectionCode Rejection,
    AgentTerminationReason? Reason, ContextCaptureStatus Capture, int Sends, int ToolEffects, int Claims,
    int HistoricalAttempts, long? HistoricalInput, long? CurrentInput, Guid? Execution, Guid? Logical,
    Guid? Physical, int? Ordinal, Guid? PreviousExecution, Guid? PreviousPhysical, int? PreviousOrdinal,
    string? WireHash, bool WireMatches, bool ResponseAssociated, string SafeDiagnostic);

internal static class PersistenceChild
{
    internal const string Reasoning = "restricted-process-reasoning-canary";
    internal const string Final = "restricted-process-final-canary";

    public static async Task<int> Main(string[] args)
    {
        // Preserve every other command, including future C1-owned routes and unknown-argument exit behavior.
        if (args.Length == 0 || args[0] != "deepseek-persistence-child") return await OrdinaryContextChild.Main(args);
        if (args.Length != 3) return 2;
        var directory = args[1]; var command = args[2]; Directory.CreateDirectory(directory);
        var producing = command.StartsWith("produce", StringComparison.Ordinal);
        var failing = command == "produce-retry" || command == "middle-failure";
        var rejecting = command.StartsWith("reject-", StringComparison.Ordinal);
        var sourceMode = producing ? command : File.ReadAllText(Path.Combine(directory, "mode.txt"));
        var reasoning = sourceMode == "produce-empty" ? "" : sourceMode == "produce-max" ? new string('r', 8191) : Reasoning;
        var tool = new EchoTool(); var hooks = new RuntimeHooks(); string? wire = null;
        var handler = new FakeHandler(async (message, token) =>
        {
            Assert.Equal(producing ? AdapterFixture.Credential : "new-process-credential", message.Headers.Authorization!.Parameter);
            wire = await message.Content!.ReadAsStringAsync(token);
            // Deliberately restricted fixture output, never ordinary stdout/results.
            File.WriteAllText(Path.Combine(directory, command + "-restricted-wire.json"), wire);
            return AdapterFixture.Http(AdapterFixture.Response(Final, reasoning), failing ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        });
        using var provider = new DeepSeekProvider(new(producing ? AdapterFixture.Credential : "new-process-credential",
            command == "reject-config" ? 2049 : 2048), handler);
        var sink = new RestrictedContextHost();
        var request = Request("old policy", producing ? "initial data" : null);
        ContextExecutionResult result;
        if (producing)
        {
            File.WriteAllText(Path.Combine(directory, "mode.txt"), command);
            result = await Agent(provider, tool, hooks).ExecuteWithContextAsync(new(request, ContextExecutionIntent.Fresh), sink);
            Assert.Equal(ContextCaptureStatus.Delivered, result.CaptureStatus);
            FileContextAuthority.Save(directory, sink.CopyRestrictedContext(), result.Checkpoint!);
        }
        else
        {
            var envelope = FileContextAuthority.Read(directory); var selected = FileContextAuthority.Selection(directory);
            if (rejecting && command != "reject-config") envelope = Alter(envelope, command);
            // A controlled trusted fixture selection lets malformed provider payloads reach the actual provider gate.
            if (rejecting && command != "reject-host") FileContextAuthority.Save(directory, envelope, selected.Info);
            var continuing = sourceMode == "produce-retry" || command == "last-retry";
            request = Request(continuing && sourceMode == "produce-retry" ? "old policy" : "new policy", continuing ? null : "next data");
            var grant = new ContextRoundGrant(Guid.NewGuid(), selected.Info, request.ExecutionId, Guid.NewGuid());
            result = await Agent(provider, tool, hooks, new FileContextAuthority(directory)).ExecuteWithContextAsync(
                new(request, continuing ? ContextExecutionIntent.ContinueRun : ContextExecutionIntent.NewRunFromContext, envelope, grant), sink);
            if (command == "reject-config")
            {
                var retryHandler = FakeHandler.Reply(AdapterFixture.Response());
                using var compatible = AdapterFixture.Provider(retryHandler);
                var replay = await Agent(compatible, tool, new RuntimeHooks(), new FileContextAuthority(directory)).ExecuteWithContextAsync(
                    new(request, ContextExecutionIntent.NewRunFromContext, envelope, grant));
                Assert.Equal(ContextAdmission.Rejected, replay.Admission); Assert.Equal(0, retryHandler.Sends);
            }
            if (command == "middle-failure")
            {
                Assert.Equal(ContextCaptureStatus.Delivered, result.CaptureStatus);
                FileContextAuthority.Save(directory, sink.CopyRestrictedContext(), result.Checkpoint!);
            }
        }
        var current = result.Outcome?.Usage?.Attempts.LastOrDefault();
        var ordinaryResult = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(Reasoning, ordinaryResult); Assert.DoesNotContain(Final, ordinaryResult);
        Assert.DoesNotContain(AdapterFixture.Credential, ordinaryResult); Assert.DoesNotContain("new-process-credential", ordinaryResult);
        var associated = false;
        if (result.CaptureStatus == ContextCaptureStatus.Delivered && !failing)
        {
            var captured = JsonNode.Parse(sink.CopyRestrictedContext().CopyRestrictedPayload())!;
            var final = captured["Records"]!.AsArray().Last(r => r!["Final"] is not null)!["Final"]!;
            var responseAttempt = final["Attempt"]!.Deserialize<ProviderAttempt>()!;
            var replayAttempt = final["Continuation"]!["Origin"]!.Deserialize<ProviderAttempt>()!;
            associated = responseAttempt.Matches(new(current!.ExecutionId, current.LogicalCallId, current.PhysicalAttemptId, current.AttemptNumber))
                && replayAttempt.Matches(responseAttempt);
        }
        if (result.CaptureStatus == ContextCaptureStatus.Delivered)
        {
            var restricted = Encoding.UTF8.GetString(sink.CopyRestrictedContext().CopyRestrictedPayload());
            Assert.DoesNotContain(AdapterFixture.Credential, restricted); Assert.DoesNotContain("new-process-credential", restricted);
        }
        var expected = PersistenceWireOracle.Root([tool.Descriptor]);
        var messages = new JsonArray(PersistenceWireOracle.Message("system", producing || sourceMode == "produce-retry" ? "old policy" : "new policy"),
            PersistenceWireOracle.Message("user", "initial data"));
        if (!producing && sourceMode != "produce-retry")
        {
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = Final, ["reasoning_content"] = reasoning });
            messages.Add(PersistenceWireOracle.Message("user", "next data"));
        }
        PersistenceWireOracle.SetMessages(expected, messages);
        var observation = new PersistenceObservation(Environment.ProcessId, result.Admission, result.RejectionCode,
            result.Outcome?.Reason, result.CaptureStatus, handler.Sends, tool.Invocations,
            Directory.GetFiles(directory, "source-*.claim").Length, result.History.Sum(r => r.Usage.Attempts.Count),
            result.History.Count == 0 ? null : result.History.Sum(r => r.Usage.InputTokens.ObservedTokens ?? 0),
            result.Outcome?.Usage?.InputTokens.ObservedTokens, current?.ExecutionId, current?.LogicalCallId,
            current?.PhysicalAttemptId, current?.AttemptNumber,
            result.Outcome?.Usage?.ContinuedCalls.SingleOrDefault()?.ExecutionId,
            result.Outcome?.Usage?.ContinuedCalls.SingleOrDefault()?.PhysicalAttemptId,
            result.Outcome?.Usage?.ContinuedCalls.SingleOrDefault()?.AttemptNumber,
            wire is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(wire))),
            wire == expected.ToJsonString(), associated, result.ToString());
        var safe = JsonSerializer.Serialize(observation);
        Assert.DoesNotContain(Reasoning, safe); Assert.DoesNotContain(Final, safe);
        Assert.DoesNotContain(AdapterFixture.Credential, safe); Assert.DoesNotContain("new-process-credential", safe);
        File.WriteAllText(Path.Combine(directory, command + "-result.json"), safe);
        return 0;
    }

    private static AgentRequest Request(string instructions, string? data) => new(Guid.NewGuid(), instructions,
        data is null ? [] : [new(AgentInputSource.Repository, data)], new(1, TimeSpan.FromSeconds(30)), AgentCapability.None,
        new AgentUsageLimits(maximumLogicalCalls: 1, maximumPhysicalDispatches: 1, retryPolicy: new(2)));

    private static IContextAgent Agent(DeepSeekProvider provider, EchoTool tool, RuntimeHooks hooks, IRuntimeContextAuthority? authority = null) =>
        (IContextAgent)RuntimeAgentFactory.Create(new(provider, [new RuntimeToolRegistration(tool, new EchoCapability())], hooks,
            contextAuthority: authority), new(requireContinuation: true));

    private static AgentContextEnvelope Alter(AgentContextEnvelope envelope, string command)
    {
        if (command == "reject-outer-format") return new(envelope.ImplementationId, 2, envelope.CompatibilityVersion, envelope.CopyRestrictedPayload());
        var value = JsonNode.Parse(envelope.CopyRestrictedPayload())!;
        switch (command)
        {
            case "reject-provider-format": value["Provider"]!["FormatVersion"] = 2; break;
            case "reject-model": value["Provider"]!["Scope"]!["Model"] = "wrong-model"; break;
            case "reject-origin": value["Provider"]!["Origin"]!["PhysicalAttemptId"] = Guid.NewGuid(); break;
            case "reject-replay":
                var final = value["Records"]!.AsArray().Last(r => r!["Final"] is not null)!["Final"]!;
                final["Continuation"]!["Bytes"] = Convert.ToBase64String([2, 65]); break;
            default:
                var bytes = Convert.FromBase64String(value["Provider"]!["Bytes"]!.GetValue<string>()); bytes[^1] ^= 1;
                value["Provider"]!["Bytes"] = Convert.ToBase64String(bytes); break;
        }
        return new(envelope.ImplementationId, envelope.FormatVersion, envelope.CompatibilityVersion, JsonSerializer.SerializeToUtf8Bytes(value));
    }
}
