using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;

namespace PackageConsumers.LiveSmoke;

internal enum Failure { Arguments, OptIn, Profile, Ci, CredentialSource, Credential, DispatchLimit, ToolLimit, Deadline, Capacity, Transport, UnexpectedReply, UnknownUsage, Authority, Child, Protocol, Confinement }
internal sealed class SmokeFailure(Failure code) : Exception { internal Failure Code { get; } = code; }
internal static class Check
{
    internal static void That([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, Failure code = Failure.UnexpectedReply)
    { if (!condition) throw new SmokeFailure(code); }
}

// This is a private, inherited pipe protocol between trusted local processes, never ordinary output.
internal sealed record Snapshot(Guid Implementation, int Format, int Compatibility, byte[] Payload, ContextCheckpointInfo Checkpoint)
{
    internal static Snapshot From(AgentContextEnvelope context, ContextCheckpointInfo checkpoint) =>
        new(context.ImplementationId, context.FormatVersion, context.CompatibilityVersion, context.CopyRestrictedPayload(), checkpoint);
    internal AgentContextEnvelope Envelope() => new(Implementation, Format, Compatibility, Payload);
    internal byte[] Fingerprint() => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this));
}
internal enum WireFault { None, Malformed, Profile, Input, History }
internal sealed record Bootstrap(bool Fresh, bool CompletedSource, Snapshot? Source, ContextRoundGrant? Grant,
    Guid Execution, Guid Target, int ToolAllowance, int MaximumTokens = 4096, int InputCharacters = 0, bool MalformedFrame = false, bool Crash = false, bool FailTool = false, WireFault WireFault = WireFault.None, bool ObserverFailure = false);
internal sealed record Report(int Pid, ContextAdmission Admission, AgentTerminationReason? Reason, ContextCaptureStatus Capture,
    int Sends, int Tools, int Imports, int Claims, int HistoryTools, int HistoryAttempts, long? Input, long? Output,
    int RuntimeAttempts, int RuntimeTools, long? HistoryInput, long? HistoryOutput,
    bool ReplyMatches, bool OrdinaryConfined, SolusAgent.Runtime.Api.Providers.ProviderError? ProviderError, Snapshot? Captured, WireObservations? Wire = null);
internal sealed record Frame(string Kind, string? Body = null, int Status = 200, bool Allowed = false,
    Snapshot? Source = null, ContextRoundGrant? Grant = null, Guid Target = default, Report? Report = null);

internal static class Pipe
{
    internal const int MaximumFrameCharacters = 524288;
    internal static async Task<string?> ReadAsync(TextReader reader, CancellationToken token)
    {
        var text = new StringBuilder(); var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), token) != 0)
        {
            if (buffer[0] == '\n') return text.ToString();
            Check.That(text.Length < MaximumFrameCharacters, Failure.Protocol);
            text.Append(buffer[0]);
        }
        Check.That(text.Length == 0, Failure.Protocol);
        return null;
    }
    internal static async Task WriteAsync<T>(TextWriter writer, T value, CancellationToken token)
    {
        var text = JsonSerializer.Serialize(value);
        Check.That(text.Length <= MaximumFrameCharacters, Failure.Capacity);
        await writer.WriteLineAsync(text.AsMemory(), token); await writer.FlushAsync(token);
    }
    internal static T Parse<T>(string? text) => JsonSerializer.Deserialize<T>(text ?? throw new SmokeFailure(Failure.Protocol))
        ?? throw new SmokeFailure(Failure.Protocol);
}

internal static class Fixture
{
    internal const string Profile = "deepseek-flash-thinking-high";
    internal const string Instruction = "Use smoke_increment exactly once. After its result, respond with exactly SMOKE_OK and no other text.";
    internal const string Final = "SMOKE_OK";
    internal const string CredentialA = "synthetic-credential-canary-A";
    internal const string CredentialB = "synthetic-credential-canary-B";
    internal const string Replay = "synthetic-restricted-replay-canary";
    internal const string PrivateError = "synthetic-private-exception-canary";
    internal const string LiveCredential = "synthetic-parent-credential-canary";
    internal static readonly string[] Canaries = [CredentialA, CredentialB, Replay, PrivateError, LiveCredential, Instruction];
    internal static bool Confined(string text) => Canaries.All(value => !text.Contains(value, StringComparison.Ordinal)
        && !text.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)), StringComparison.Ordinal));
}
