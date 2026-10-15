using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;

namespace PackageConsumers.LiveSmoke;

internal delegate Task<WireReply> Send(string request, bool fresh, CancellationToken token);
internal sealed record WireReply(int Status, string Body);

// One supervisor owns all process rounds. Neither the serialized context nor the child renews these allowances.
internal sealed class SmokeHost : IDisposable
{
    private readonly Send send;
    private readonly SafeFailureFacts? facts;
    private readonly CancellationTokenSource deadline;
    private readonly int maximumSends, maximumTools;
    private readonly HashSet<Guid> consumedSources = [];
    private readonly Guid target = Guid.NewGuid();
    private Snapshot? selected;
    private Bootstrap? lastBootstrap;
    private JsonArray? expectedHistory;
    private bool completedSource;
    internal string DirectoryPath { get; }
    internal int Sends { get; private set; }
    internal int ToolEntries { get; private set; }
    internal int Effects { get; private set; }
    internal int Claims { get; private set; }
    internal bool ChildTerminated { get; private set; }

    internal SmokeHost(Send send, int maximumSends = 6, int maximumTools = 3, TimeSpan? duration = null, SafeFailureFacts? facts = null)
    {
        Check.That(maximumSends is > 0 and <= 6 && maximumTools is >= 0 and <= 3, Failure.Capacity);
        this.facts = facts; this.send = send; this.maximumSends = maximumSends; this.maximumTools = maximumTools;
        var elapsed = duration ?? TimeSpan.FromMinutes(5);
        Check.That(elapsed > TimeSpan.Zero && elapsed <= TimeSpan.FromMinutes(5), Failure.Capacity);
        deadline = new(elapsed);
        DirectoryPath = Path.Combine(Path.GetTempPath(), "solus-smoke-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) throw new SmokeFailure(Failure.Capacity);
        Directory.CreateDirectory(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal async Task<Report> RunAsync(bool fresh, bool tamper = false, bool changedTarget = false,
        bool providerMismatch = false, bool replay = false, bool expectRejected = false,
        int inputCharacters = 0, bool malformedFrame = false, bool crash = false, bool failTool = false, WireFault wireFault = WireFault.None)
    {
        facts?.BeginRound(fresh);
        var token = deadline.Token;
        if (token.IsCancellationRequested) throw new SmokeFailure(Failure.Deadline);
        var source = fresh ? null : ReadContext();
        if (tamper) source = source! with { Payload = [.. source.Payload, (byte)' '] }; // Legal JSON, wrong trusted capture bytes.
        var execution = Guid.NewGuid();
        var grant = fresh ? null : new ContextRoundGrant(Guid.NewGuid(), selected!.Checkpoint, execution, Guid.NewGuid());
        var boot = replay ? lastBootstrap! : new Bootstrap(fresh, completedSource, source, grant, execution,
            changedTarget ? Guid.NewGuid() : target, maximumTools - ToolEntries, providerMismatch ? 4095 : 4096,
            inputCharacters, malformedFrame, crash, failTool, wireFault);
        Check.That(boot is not null, Failure.Authority);
        lastBootstrap = boot;
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = DirectoryPath,
        };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location); start.ArgumentList.Add("--child");
        // No ambient credential, proxy, telemetry or diagnostic configuration is inherited by adapter children.
        start.Environment.Clear(); start.Environment["DOTNET_NOLOGO"] = "1"; start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(Environment.ProcessPath!);
        using var process = Process.Start(start) ?? throw new SmokeFailure(Failure.Child);
        var error = ReadErrorAsync(process.StandardError, token);
        Report? report = null;
        var stageSends = 0; var stageTools = 0;
        var enteredCalls = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            await Pipe.WriteAsync(process.StandardInput, boot, token);
            while (await Pipe.ReadAsync(process.StandardOutput, token) is { } text)
            {
                var frame = Pipe.Parse<Frame>(text);
                Check.That(report is null, Failure.Protocol);
                Frame response;
                switch (frame.Kind)
                {
                    case "send":
                        if (facts is not null) facts.RequestProfileMatch = null;
                        Check.That(Sends < maximumSends, Failure.DispatchLimit);
                        CheckWire(frame.Body!, boot.Fresh);
                        // Charge before entering transport. A thrown/unknown request never refunds this slot.
                        Sends++; stageSends++; facts?.BeginDispatch();
                        WireReply reply;
                        try { reply = await send(frame.Body!, boot.Fresh, token).WaitAsync(token); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (SmokeFailure) { throw; }
                        catch { throw new SmokeFailure(Failure.Transport); }
                        facts?.ObserveStatus(reply.Status);
                        Check.That(Encoding.UTF8.GetByteCount(reply.Body) <= 65536, Failure.Capacity);
                        try { ObserveReply(reply, boot.Fresh); }
                        catch (JsonException) { throw new SmokeFailure(Failure.UnexpectedReply); }
                        catch (InvalidOperationException) { throw new SmokeFailure(Failure.UnexpectedReply); }
                        response = new("reply", reply.Body, reply.Status);
                        break;
                    case "tool":
                        Check.That(frame.Target == target, Failure.Authority);
                        Check.That(ToolEntries < maximumTools, Failure.ToolLimit);
                        Check.That(frame.Body is not null && enteredCalls.Add(frame.Body), Failure.Authority);
                        ToolEntries++; stageTools++;
                        response = new("reply", Allowed: true);
                        break;
                    case "effect":
                        Check.That(frame.Target == target && frame.Body is not null && enteredCalls.Remove(frame.Body), Failure.Authority);
                        Effects++;
                        response = new("reply", Allowed: true);
                        break;
                    case "claim":
                        var allowed = selected is not null && frame.Source is not null && frame.Grant is not null
                            && frame.Target == target && SameGrant(frame.Grant, boot.Grant)
                            && !consumedSources.Contains(selected.Checkpoint.RoundId)
                            && CryptographicOperations.FixedTimeEquals(selected.Fingerprint(), frame.Source.Fingerprint());
                        if (allowed) { consumedSources.Add(selected!.Checkpoint.RoundId); Claims++; }
                        response = new("reply", Allowed: allowed);
                        break;
                    case "report":
                        report = frame.Report ?? throw new SmokeFailure(Failure.Protocol);
                        facts?.ObserveReport(report); continue;
                    default: throw new SmokeFailure(Failure.Protocol);
                }
                await Pipe.WriteAsync(process.StandardInput, response, token);
            }
            await process.WaitForExitAsync(token);
            Check.That(process.ExitCode == 0 && await error == 0 && report is not null, Failure.Child);
            Check.That(report!.Pid == process.Id && report.OrdinaryConfined, Failure.Confinement);
            Check.That(report.Sends == stageSends && report.Tools == stageTools, Failure.Protocol);
            Check.That(report.RuntimeAttempts == stageSends && report.RuntimeTools == stageTools, Failure.Protocol);
            if (expectRejected)
            {
                Check.That(report.Admission == ContextAdmission.Rejected && stageSends == 0 && stageTools == 0
                    && report.Captured is null, Failure.Authority);
                return report;
            }
            Check.That(report.Admission == (boot.Fresh ? ContextAdmission.Fresh : ContextAdmission.Supplied), Failure.Authority);
            if (report.ProviderError == SolusAgent.Runtime.Api.Providers.ProviderError.LimitExceeded
                || report.Sends == 0 && report.Reason == AgentTerminationReason.ResourceLimit)
                throw new SmokeFailure(Failure.Capacity);
            if (!report.Input.HasValue || !report.Output.HasValue) throw new SmokeFailure(Failure.UnknownUsage);
            Check.That(report.Output <= 4096 && report.Input <= 98304, Failure.Capacity);
            if (stageSends == 0 && Sends == maximumSends) throw new SmokeFailure(Failure.DispatchLimit);
            if (boot.Fresh && stageTools == 0 && ToolEntries == maximumTools) throw new SmokeFailure(Failure.ToolLimit);
            Check.That(report.ReplyMatches && report.Reason == (boot.Fresh ? AgentTerminationReason.ResourceLimit : AgentTerminationReason.Completed)
                && report.Sends == 1 && report.Tools == (boot.Fresh ? 1 : 0), Failure.UnexpectedReply);
            Check.That(boot.Fresh ? report.Imports == 0 && report.Claims == 0
                : report.Imports == 1 && report.Claims == 1 && report.HistoryTools >= 1 && report.HistoryAttempts >= 1, Failure.Authority);
            Check.That(report.Capture == ContextCaptureStatus.Delivered && report.Captured is not null, Failure.Capacity);
            Pin(report.Captured!); completedSource = !boot.Fresh;
            return report;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw new SmokeFailure(Failure.Deadline); }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None); ChildTerminated = true;
            }
            try { await error; } catch (OperationCanceledException) { }
        }
    }

    private void Pin(Snapshot snapshot)
    {
        if (OperatingSystem.IsWindows()) throw new SmokeFailure(Failure.Capacity);
        Check.That(snapshot.Payload.Length is > 0 and <= 196608, Failure.Capacity);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        Check.That(bytes.Length <= 400000, Failure.Capacity);
        // Trusted selection is retained only in parent memory, separately from the untrusted storage reader.
        selected = snapshot with { Payload = snapshot.Payload.ToArray() };
        using var file = new FileStream(Path.Combine(DirectoryPath, "context.json"), new FileStreamOptions
        {
            Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        file.Write(bytes);
    }
    private Snapshot ReadContext()
    {
        Check.That(selected is not null, Failure.Authority);
        var path = Path.Combine(DirectoryPath, "context.json");
        Check.That(new FileInfo(path).Length <= 400000, Failure.Capacity);
        return Pipe.Parse<Snapshot>(File.ReadAllText(path));
    }
    private static bool SameGrant(ContextRoundGrant left, ContextRoundGrant? right) => right is not null
        && left.GrantId == right.GrantId && left.Source == right.Source && left.ExecutionId == right.ExecutionId && left.RoundId == right.RoundId;
    private static async Task<int> ReadErrorAsync(TextReader reader, CancellationToken token)
    {
        var count = 0; var buffer = new char[256]; int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        { count += read; Check.That(count <= 4096, Failure.Protocol); }
        return count; // Contents are deliberately never retained or printed.
    }
    private void CheckWire(string body, bool fresh)
    {
        Check.That(Encoding.UTF8.GetByteCount(body) <= 65536, Failure.Capacity);
        var node = JsonNode.Parse(body)!;
        var profileMatches = node["model"]!.GetValue<string>() == "deepseek-flash" && node["max_tokens"]!.GetValue<int>() == 4096
            && !node["stream"]!.GetValue<bool>() && node["thinking"]!["type"]!.GetValue<string>() == "enabled"
            && node["reasoning_effort"]!.GetValue<string>() == "high";
        if (facts is not null) facts.RequestProfileMatch = profileMatches;
        Check.That(profileMatches, Failure.Profile);
        var messages = node["messages"]!.AsArray();
        Check.That(messages.Count <= 32 && messages[0]!["role"]!.GetValue<string>() == "system"
            && messages[0]!["content"]!.GetValue<string>() == Fixture.Instruction, Failure.Capacity);
        Check.That(node["tools"]!.AsArray().Count == 1 && node["tools"]![0]!["function"]!["name"]!.GetValue<string>() == "smoke_increment");
        if (fresh) { Check.That(messages.Count == 1); expectedHistory = []; }
        else
        {
            Check.That(expectedHistory is not null && messages.Count == expectedHistory.Count + 1, Failure.Authority);
            for (var index = 0; index < expectedHistory!.Count; index++)
                Check.That(JsonNode.DeepEquals(expectedHistory[index], messages[index + 1]), Failure.Authority);
        }
    }
    private void ObserveReply(WireReply reply, bool fresh)
    {
        if (reply.Status != 200) return; // Let the actual adapter classify errors, with no retry policy.
        var root = JsonNode.Parse(reply.Body)!;
        if (facts is not null) facts.ResponseProfileMatch = root["model"] is JsonValue model
            && model.TryGetValue<string>(out var name) && name == "deepseek-flash";
        var choice = root["choices"]![0]!; var message = choice["message"]!.DeepClone();
        // Remove a wire-only null field which the adapter intentionally omits on replay.
        if (message["tool_calls"] is null) message.AsObject().Remove("tool_calls");
        if (fresh)
        {
            if (choice["finish_reason"]!.GetValue<string>() != "tool_calls") return;
            expectedHistory!.Add(message);
            var calls = message["tool_calls"]!.AsArray();
            if (calls.Count != 1) return;
            expectedHistory.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = calls[0]!["id"]!.GetValue<string>(), ["content"] = "{\"done\":true}" });
        }
        else expectedHistory!.Add(message);
    }
    public void Dispose()
    {
        deadline.Dispose();
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
    }
}
