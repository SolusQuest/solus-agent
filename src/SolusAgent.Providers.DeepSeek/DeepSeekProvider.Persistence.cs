using System.Security.Cryptography;
using System.Text;
using SolusAgent.Runtime.Api.Providers;

namespace SolusAgent.Providers.DeepSeek;

public sealed partial class DeepSeekProvider
{
    /// <summary>Exports the current profile and exact checkpoint/replay binding without retaining credentials or live objects.</summary>
    /// <remarks>The runtime separately retains exact history and replay. This consistency witness is not authentication or dispatch authority.</remarks>
    public ProviderSavedState? ExportContext(ProviderContextBinding binding)
    {
        if (Volatile.Read(ref disposed) != 0 || !ValidBinding(binding)) return null;
        return new(binding.Scope, binding.Origin, 1, ContextBytes(binding));
    }

    /// <summary>Checks the current provider format, configuration and exact retained replay after trusted Host admission, without effects.</summary>
    public bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state)
    {
        if (Volatile.Read(ref disposed) != 0 || state is null || !ValidBinding(binding)
            || state.FormatVersion != 1 || state.PayloadByteCount != 36
            || !Scope.Matches(state.Scope) || !binding.Origin.Matches(state.Origin)) return false;
        return CryptographicOperations.FixedTimeEquals(state.CopyRestrictedPayload(), ContextBytes(binding));
    }

    private bool ValidBinding(ProviderContextBinding binding)
    {
        if (binding is null || binding.Origin is null || !Scope.Matches(binding.Scope)) return false;
        if (binding.Continuation is not { } replay) return true;
        if (!Scope.Matches(replay.Scope)) return false;
        // The last attempted origin may differ from the last accepted continuation, including across rounds.
        // The Host/runtime, which owns the complete inventory, must admit that historical lineage.
        try { _ = DeepSeekReplay.Decode(replay); return true; }
        catch (ProviderContractException) { return false; }
    }

    private byte[] ContextBytes(ProviderContextBinding binding)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("deepseek-flash-thinking-high-context-v1");
            writer.Write(options.Endpoint.OriginalString);
            writer.Write(options.MaximumTokens);
            writer.Write(options.MaximumRequestBodyBytes);
            writer.Write(options.MaximumResponseBodyBytes);
            writer.Write(options.Timeout.Ticks);
            WriteScope(binding.Scope); WriteAttempt(binding.Origin);
            writer.Write(binding.Continuation is not null);
            if (binding.Continuation is { } replay)
            {
                WriteScope(replay.Scope); WriteAttempt(replay.Origin);
                var bytes = replay.CopyReplayBytes(); writer.Write(bytes.Length); writer.Write(bytes);
            }
            void WriteScope(ProviderScope scope) { writer.Write(scope.Provider); writer.Write(scope.Model); }
            void WriteAttempt(ProviderAttempt attempt)
            {
                writer.Write(attempt.ExecutionId.ToByteArray()); writer.Write(attempt.LogicalCallId.ToByteArray());
                writer.Write(attempt.PhysicalAttemptId.ToByteArray()); writer.Write(attempt.AttemptNumber);
            }
        }
        // Current payload grammar: ASCII DSCP followed by SHA-256; FormatVersion is external metadata.
        return [.. "DSCP"u8, .. SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)))];
    }
}
