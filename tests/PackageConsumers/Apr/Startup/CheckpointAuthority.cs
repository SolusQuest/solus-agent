using System.Security.Cryptography;
using System.Text.Json;
using PackageConsumers.Apr.Business;
using SolusAgent.Api.Context;
using SolusAgent.Runtime.Api.Configuration;

namespace PackageConsumers.Apr.Startup;

internal sealed record StoredEnvelope(Guid ImplementationId, int FormatVersion, int CompatibilityVersion, byte[] Bytes)
{
    internal AgentContextEnvelope Open() => new(ImplementationId, FormatVersion, CompatibilityVersion, Bytes);
}
internal sealed record Selection(ContextCheckpointInfo Checkpoint, byte[] Fingerprint, Guid TargetId);

// Sequential synthetic Host authority; the selected fingerprint/target never come from the envelope reader.
internal sealed class CheckpointAuthority(string root, string source, Guid actualTarget) : IRuntimeContextAuthority
{
    internal int Calls { get; private set; }
    internal int Claims { get; private set; }
    internal static byte[] Fingerprint(AgentContextEnvelope envelope) => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        new StoredEnvelope(envelope.ImplementationId, envelope.FormatVersion, envelope.CompatibilityVersion, envelope.CopyRestrictedPayload())));
    internal static void Save(string directory, AgentContextEnvelope envelope, ContextCheckpointInfo checkpoint, Guid target)
    {
        Directory.CreateDirectory(directory);
        Store.Write(Path.Combine(directory, "envelope.json"), new StoredEnvelope(envelope.ImplementationId,
            envelope.FormatVersion, envelope.CompatibilityVersion, envelope.CopyRestrictedPayload()));
        Store.Write(Path.Combine(directory, "selection.json"), new Selection(checkpoint, Fingerprint(envelope), target));
    }
    public bool TryClaim(AgentContextEnvelope context, ContextRoundGrant grant)
    {
        Calls++;
        var selected = Store.Read<Selection>(Path.Combine(source, "selection.json"));
        if (selected.Checkpoint != grant.Source || selected.TargetId != actualTarget
            || !CryptographicOperations.FixedTimeEquals(selected.Fingerprint, Fingerprint(context))) return false;
        var directory = Path.Combine(root, "claims"); Directory.CreateDirectory(directory);
        try
        {
            Store.WriteOnce(Path.Combine(directory, $"source-{grant.Source.LogicalWorkId}-{grant.Source.RoundId}.claim"), grant);
            Store.WriteOnce(Path.Combine(directory, $"grant-{grant.GrantId}.claim"), grant);
            Claims++; return true;
        }
        catch (IOException) { return false; }
    }
}
