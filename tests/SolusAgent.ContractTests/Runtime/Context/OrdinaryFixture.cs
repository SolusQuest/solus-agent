using System.Security.Cryptography;
using System.Text.Json;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Startup;

namespace SolusAgent.ContractTests.Runtime.Context;

internal static class OrdinaryFixture
{
    internal static AgentRequest Request(Guid? id = null, string instructions = "CURRENT_HOST_INSTRUCTIONS", IReadOnlyList<AgentInput>? data = null,
        AgentUsageLimits? limits = null) => new(id ?? Guid.NewGuid(), instructions, data ?? [], new(1, TimeSpan.FromSeconds(30)),
            AgentCapability.None, limits ?? new(1, 1, 3, 2, 1, new(new(3, 2), 3, 2), new(2)));
    internal static IContextAgent Agent(PersistentProvider provider, IRuntimeContextAuthority? authority = null, RuntimeHooks? hooks = null,
        RuntimeOptions? options = null) => (IContextAgent)RuntimeAgentFactory.Create(new(provider, [], hooks ?? new(), contextAuthority: authority),
            options ?? new(requireContinuation: true));
    internal static ContextRoundGrant Grant(ContextExecutionResult source, Guid execution) => new(Guid.NewGuid(), source.Checkpoint!, execution, Guid.NewGuid());
    internal static byte[] Fingerprint(AgentContextEnvelope envelope) => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { envelope.ImplementationId, envelope.FormatVersion, envelope.CompatibilityVersion, Bytes = envelope.CopyRestrictedPayload() }));
    internal static async Task<(ContextExecutionResult Result, AgentContextEnvelope Envelope)> Capture(bool fail = true, RuntimeHooks? hooks = null,
        RuntimeOptions? options = null, PersistentProvider? provider = null, AgentRequest? request = null)
    {
        var sink = new RestrictedContextHost();
        var result = await Agent(provider ?? new(fail), hooks: hooks, options: options).ExecuteWithContextAsync(
            new(request ?? Request(), ContextExecutionIntent.Fresh), sink);
        return (result, sink.CopyRestrictedContext());
    }
}
internal sealed class SelectedAuthority(AgentContextEnvelope trusted, ContextCheckpointInfo source) : IRuntimeContextAuthority
{
    private readonly byte[] expected = OrdinaryFixture.Fingerprint(trusted);
    private int claimed;
    public int Claims { get; private set; }
    public bool TryClaim(AgentContextEnvelope context, ContextRoundGrant grant)
    {
        Claims++;
        return grant.Source == source && CryptographicOperations.FixedTimeEquals(expected, OrdinaryFixture.Fingerprint(context))
            && Interlocked.CompareExchange(ref claimed, 1, 0) == 0;
    }
}
internal sealed class FileContextAuthority(string directory) : IRuntimeContextAuthority
{
    internal sealed record StoredEnvelope(Guid ImplementationId, int FormatVersion, int CompatibilityVersion, byte[] Bytes);
    internal sealed record TrustedSelection(ContextCheckpointInfo Info, byte[] Fingerprint);
    internal static void Save(string directory, AgentContextEnvelope envelope, ContextCheckpointInfo info)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "checkpoint.json"), JsonSerializer.Serialize(new StoredEnvelope(envelope.ImplementationId,
            envelope.FormatVersion, envelope.CompatibilityVersion, envelope.CopyRestrictedPayload())));
        // This separate fixture-owned trusted file is never supplied by the untrusted envelope reader.
        File.WriteAllText(Path.Combine(directory, "trusted-selection.json"), JsonSerializer.Serialize(new TrustedSelection(info, OrdinaryFixture.Fingerprint(envelope))));
    }
    internal static AgentContextEnvelope Read(string directory)
    {
        var value = JsonSerializer.Deserialize<StoredEnvelope>(File.ReadAllText(Path.Combine(directory, "checkpoint.json")))!;
        return new(value.ImplementationId, value.FormatVersion, value.CompatibilityVersion, value.Bytes);
    }
    internal static TrustedSelection Selection(string directory) => JsonSerializer.Deserialize<TrustedSelection>(
        File.ReadAllText(Path.Combine(directory, "trusted-selection.json")))!;
    public bool TryClaim(AgentContextEnvelope context, ContextRoundGrant grant)
    {
        var selected = Selection(directory);
        if (selected.Info != grant.Source || !CryptographicOperations.FixedTimeEquals(selected.Fingerprint, OrdinaryFixture.Fingerprint(context))) return false;
        try
        {
            // Source/round exclusivity also rejects different grant IDs and competing checkpoints of the selected logical work.
            using var source = new FileStream(Path.Combine(directory, $"source-{grant.Source.LogicalWorkId}-{grant.Source.RoundId}.claim"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            source.Write(JsonSerializer.SerializeToUtf8Bytes(grant)); source.Flush(true);
            using var claim = new FileStream(Path.Combine(directory, $"grant-{grant.GrantId}.claim"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            claim.WriteByte(1); claim.Flush(true);
            return true;
        }
        catch (IOException) { return false; }
    }
}
