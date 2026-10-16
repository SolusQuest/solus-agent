using System.Text;
using System.Text.Json;

namespace PackageConsumers.LiveSmoke;

internal enum FakeMode { Normal, RateLimited, Unavailable, Throw, Hold, EarlyFinal, WrongTool, Malformed, ReplayEmpty, ReplayExact, ReplayOverflow, ResponseExact, ResponseOverflow, OutputExact, OutputOverflow, UnknownUsage, WhitespaceArguments, NonemptyArguments, MalformedArguments, ArrayArguments, UsageConflict, UnknownRoot }
internal sealed class FakeTransport(FakeMode mode = FakeMode.Normal)
{
    internal int Entries;
    internal Task<WireReply> SendAsync(string body, bool fresh, CancellationToken token)
    {
        Entries++;
        if (mode == FakeMode.Hold) return new TaskCompletionSource<WireReply>().Task; // Intentionally ignores cancellation.
        if (mode == FakeMode.Throw) throw new HttpRequestException(Fixture.PrivateError + Fixture.CredentialA + Fixture.Replay);
        if (mode == FakeMode.Malformed) return Task.FromResult(new WireReply(200, "not-json-" + Fixture.PrivateError));
        if (mode == FakeMode.ResponseOverflow) return Task.FromResult(new WireReply(200, new string('x', 65537)));
        var tool = fresh && mode != FakeMode.EarlyFinal;
        var response = JsonSerializer.Serialize(new
        {
            id = "synthetic-smoke-response", @object = "chat.completion", model = "deepseek-flash",
            choices = new[] { new { index = 0, finish_reason = tool ? "tool_calls" : "stop", message = new
            {
                role = "assistant", content = tool ? null : Fixture.Final,
                reasoning_content = mode == FakeMode.ReplayOverflow ? new string('r', 8192) : mode == FakeMode.ReplayExact ? new string('r', 8191)
                    : mode == FakeMode.ReplayEmpty ? "" : Fixture.Replay,
                tool_calls = tool ? new[] { new { id = "smoke-call-" + Entries, type = "function", function = new
                { name = mode == FakeMode.WrongTool ? "uninstalled_tool" : "smoke_increment", arguments = mode == FakeMode.WhitespaceArguments ? " \n{ } \t"
                    : mode == FakeMode.NonemptyArguments ? "{\"extra\":true}" : mode == FakeMode.MalformedArguments ? "{"
                    : mode == FakeMode.ArrayArguments ? "[]" : "{}" } } } : null,
            } } },
            usage = mode == FakeMode.UnknownUsage ? null : new
            { prompt_tokens = 10, completion_tokens = mode == FakeMode.OutputOverflow ? 4097 : mode == FakeMode.OutputExact ? 4096 : 5,
                total_tokens = mode == FakeMode.OutputOverflow ? 4107 : mode == FakeMode.OutputExact ? 4106 : 15 },
        });
        if (mode == FakeMode.UsageConflict || mode == FakeMode.UnknownRoot)
        {
            var changed = System.Text.Json.Nodes.JsonNode.Parse(response)!;
            if (mode == FakeMode.UsageConflict) changed["usage"]!["total_tokens"] = 16;
            else changed[Fixture.PrivateError] = Fixture.CredentialA;
            response = changed.ToJsonString();
        }
        if (mode == FakeMode.ResponseExact) response = response.PadRight(65536);
        return Task.FromResult(new WireReply(mode == FakeMode.RateLimited ? 429 : mode == FakeMode.Unavailable ? 503 : 200, response));
    }
}

internal static class SelfChecks
{
    internal static async Task<int> RunAsync()
    {
        if (OperatingSystem.IsWindows()) throw new SmokeFailure(Failure.Capacity);
        var cases = 0;
        async Task Case(string name, Func<Task> body)
        {
            await body(); cases++;
        }
        await Case("installed-package-process-flow", async () =>
        {
            var fake = new FakeTransport(); string path;
            using (var host = new SmokeHost(fake.SendAsync))
            {
                path = host.DirectoryPath;
                var first = await host.RunAsync(true); var second = await host.RunAsync(false);
                Check.That(first.Pid != second.Pid && first.Sends == 1 && first.Tools == 1 && first.HistoryAttempts == 0
                    && second.Sends == 1 && second.Tools == 0 && second.HistoryTools == 1 && second.HistoryAttempts == 1);
                Check.That(first.Input == 10 && first.Output == 5 && second.Input == 10 && second.Output == 5);
                Check.That(first.HistoryInput == 0 && first.HistoryOutput == 0 && second.HistoryInput == 10 && second.HistoryOutput == 5);
                var safe = SafeSummary.Create(first with { Pid = 987654321 }, second, host.Sends, host.ToolEntries, host.Effects, TimeSpan.FromSeconds(1), false);
                Check.That(Fixture.Confined(safe) && !safe.Contains("987654321", StringComparison.Ordinal));
                using var measured = JsonDocument.Parse(safe);
                Check.That(measured.RootElement.GetProperty("currentInputTokens").GetInt64() == 20
                    && measured.RootElement.GetProperty("restorer").GetProperty("historicalInputTokens").GetInt64() == 10);
                using var unknown = JsonDocument.Parse(SafeSummary.Create(first with { Input = null, Output = null }, second,
                    host.Sends, host.ToolEntries, host.Effects, TimeSpan.FromSeconds(1), false));
                Check.That(unknown.RootElement.GetProperty("currentInputTokens").ValueKind == JsonValueKind.Null
                    && unknown.RootElement.GetProperty("currentOutputTokens").ValueKind == JsonValueKind.Null);
                Check.That(host.Sends == 2 && fake.Entries == 2 && host.ToolEntries == 1 && host.Effects == 1 && host.Claims == 1);
                Check.That(File.Exists(Path.Combine(path, "context.json")) && first.OrdinaryConfined && second.OrdinaryConfined);
                Check.That(File.GetUnixFileMode(Path.Combine(path, "context.json")) == (UnixFileMode.UserRead | UnixFileMode.UserWrite));
            }
            Check.That(!Directory.Exists(path), Failure.Confinement);
        });
        await Case("aggregate-six-dispatches", async () =>
        {
            var fake = new FakeTransport(); using var host = new SmokeHost(fake.SendAsync);
            await host.RunAsync(true); await host.RunAsync(false);
            for (var round = 0; round < 4; round++) await host.RunAsync(false);
            Check.That(host.Sends == 6 && fake.Entries == 6 && host.ToolEntries == 1 && host.Effects == 1);
            await Fails(Failure.DispatchLimit, () => host.RunAsync(true));
            Check.That(host.Sends == 6 && fake.Entries == 6 && host.ToolEntries == 1);
        });
        await Case("exact-continuation-capacity", async () =>
        {
            var fake = new FakeTransport(FakeMode.ReplayExact); using var host = new SmokeHost(fake.SendAsync);
            await host.RunAsync(true); await host.RunAsync(false);
            Check.That(host.Sends == 2 && host.ToolEntries == 1 && host.Effects == 1);
        });
        foreach (var mode in new[] { FakeMode.ReplayEmpty, FakeMode.ResponseExact, FakeMode.OutputExact, FakeMode.WhitespaceArguments })
            await Case("positive-boundary-" + mode, async () =>
            {
                var fake = new FakeTransport(mode); using var host = new SmokeHost(fake.SendAsync);
                await host.RunAsync(true); await host.RunAsync(false);
                Check.That(host.Sends == 2 && host.ToolEntries == 1 && host.Effects == 1);
            });
        await Case("independent-parent-dispatch-cut", async () =>
        {
            var fake = new FakeTransport(); using var host = new SmokeHost(fake.SendAsync, maximumSends: 2);
            await host.RunAsync(true); await host.RunAsync(false);
            await Fails(Failure.DispatchLimit, () => host.RunAsync(false));
            Check.That(host.Sends == 2 && fake.Entries == 2);
        });
        await Case("aggregate-three-tool-entries", async () =>
        {
            var fake = new FakeTransport(); using var host = new SmokeHost(fake.SendAsync);
            for (var round = 0; round < 3; round++) await host.RunAsync(true);
            await Fails(Failure.ToolLimit, () => host.RunAsync(true));
            Check.That(host.ToolEntries == 3 && host.Effects == 3 && host.Sends == 4 && fake.Entries == 4);
        });
        await Case("independent-parent-tool-cut", async () =>
        {
            var fake = new FakeTransport(); using var host = new SmokeHost(fake.SendAsync, maximumTools: 1);
            await host.RunAsync(true); await Fails(Failure.ToolLimit, () => host.RunAsync(true));
            Check.That(host.ToolEntries == 1 && host.Effects == 1 && fake.Entries == 2);
        });
        await Case("failed-tool-entry-is-charged", async () =>
        {
            var fake = new FakeTransport(); using var host = new SmokeHost(fake.SendAsync);
            await Fails(Failure.UnexpectedReply, () => host.RunAsync(true, failTool: true));
            Check.That(host.ToolEntries == 1 && host.Effects == 0 && host.Sends == 1);
        });
        foreach (var mutation in new[] { "bytes", "target", "provider", "replay" })
            await Case("authority-" + mutation, async () =>
            {
                var fake = new FakeTransport(); using var host = new SmokeHost(fake.SendAsync);
                await host.RunAsync(true);
                if (mutation == "replay") await host.RunAsync(false);
                var before = host.Sends; var tools = host.ToolEntries;
                var rejected = await host.RunAsync(false, tamper: mutation == "bytes", changedTarget: mutation == "target",
                    providerMismatch: mutation == "provider", replay: mutation == "replay", expectRejected: true);
                Check.That(rejected.Sends == 0 && rejected.Tools == 0 && host.Sends == before && host.ToolEntries == tools);
                Check.That(rejected.Claims == (mutation == "provider" ? 1 : 0) && rejected.Imports == (mutation == "provider" ? 1 : 0));
            });
        foreach (var (mode, failure) in new[]
        {
            (FakeMode.RateLimited, Failure.UnexpectedReply), (FakeMode.Unavailable, Failure.UnexpectedReply),
            (FakeMode.Throw, Failure.Transport), (FakeMode.EarlyFinal, Failure.UnexpectedReply),
            (FakeMode.WrongTool, Failure.UnexpectedReply), (FakeMode.Malformed, Failure.UnexpectedReply),
            (FakeMode.ReplayOverflow, Failure.Capacity), (FakeMode.ResponseOverflow, Failure.Capacity),
            (FakeMode.OutputOverflow, Failure.Capacity), (FakeMode.UnknownUsage, Failure.UnknownUsage),
            (FakeMode.NonemptyArguments, Failure.UnexpectedReply), (FakeMode.MalformedArguments, Failure.UnexpectedReply),
            (FakeMode.ArrayArguments, Failure.UnexpectedReply),
        })
            await Case("response-" + mode, async () =>
            {
                var fake = new FakeTransport(mode); string path; var facts = new SafeFailureFacts();
                using (var host = new SmokeHost(fake.SendAsync, facts: facts))
                {
                    path = host.DirectoryPath;
                    await Fails(failure, () => host.RunAsync(true));
                    Check.That(host.Sends == 1 && fake.Entries == 1);
                    using var observed = JsonDocument.Parse(facts.Envelope(failure, host).Split('\n')[1]["LiveSmoke facts: ".Length..]);
                    var value = observed.RootElement;
                    Check.That(value.GetProperty("stage").GetString() == "producer"
                        && value.GetProperty("dispatches").GetInt32() == 1
                        && value.GetProperty("requestProfileMatch").GetBoolean());
                    Check.That(Fixture.Confined(facts.Envelope(failure, host)));
                    if (mode is FakeMode.RateLimited or FakeMode.Unavailable)
                        Check.That(value.GetProperty("httpStatus").GetInt32() == (mode == FakeMode.RateLimited ? 429 : 503));
                    if (mode is FakeMode.Throw or FakeMode.UnknownUsage or FakeMode.Malformed)
                        Check.That(value.GetProperty("currentInputTokens").ValueKind == JsonValueKind.Null);
                    // No retries, including 429/503/unknown transport.
                }
                Check.That(!Directory.Exists(path), Failure.Confinement);
            });
        await Case("input-capacity-before-send", async () =>
        {
            var fake = new FakeTransport(); using var host = new SmokeHost(fake.SendAsync);
            await Fails(Failure.Capacity, () => host.RunAsync(true, inputCharacters: 17000));
            Check.That(host.Sends == 0 && fake.Entries == 0 && host.ToolEntries == 0);
        });
        await Case("whole-smoke-watchdog", async () =>
        {
            var fake = new FakeTransport(FakeMode.Hold); string path; var facts = new SafeFailureFacts();
            using (var host = new SmokeHost(fake.SendAsync, duration: TimeSpan.FromSeconds(2), facts: facts))
            {
                path = host.DirectoryPath;
                await Fails(Failure.Deadline, () => host.RunAsync(true));
                Check.That(fake.Entries == 1 && host.Sends == 1 && host.ChildTerminated && host.ToolEntries == 0);
                using var observed = JsonDocument.Parse(facts.Envelope(Failure.Deadline, host).Split('\n')[1]["LiveSmoke facts: ".Length..]);
                Check.That(observed.RootElement.GetProperty("dispatches").GetInt32() == 1
                    && observed.RootElement.GetProperty("currentInputTokens").ValueKind == JsonValueKind.Null);
            }
            Check.That(!Directory.Exists(path), Failure.Confinement);
        });
        foreach (var malformed in new[] { true, false })
            await Case(malformed ? "private-rpc-failure" : "child-exception-confinement", async () =>
            {
                var fake = new FakeTransport(); var facts = new SafeFailureFacts(); using var host = new SmokeHost(fake.SendAsync, facts: facts);
                await Fails(malformed ? Failure.Protocol : Failure.Child, () => host.RunAsync(true, malformedFrame: malformed, crash: !malformed));
                Check.That(host.Sends == 0 && host.ToolEntries == 0);
                using var observed = JsonDocument.Parse(facts.Envelope(Failure.Child, host).Split('\n')[1]["LiveSmoke facts: ".Length..]);
                Check.That(observed.RootElement.GetProperty("providerError").ValueKind == JsonValueKind.Null
                    && observed.RootElement.GetProperty("currentInputTokens").ValueKind == JsonValueKind.Null);
            });
        await Case("offline-ambient-key-poison", async () =>
        {
            var fake = new FakeTransport(); var loads = 0; var liveFactories = 0;
            var transport = LiveGate.ChooseTransport([], _ => Fixture.LiveCredential,
                _ => { loads++; throw new SmokeFailure(Failure.Credential); }, _ => { liveFactories++; throw new SmokeFailure(Failure.Transport); }, fake.SendAsync);
            using var host = new SmokeHost(transport); await host.RunAsync(true); await host.RunAsync(false);
            Check.That(loads == 0 && liveFactories == 0 && fake.Entries == 2);
        });
        await Case("controlled-live-gate", async () =>
        {
            var fake = new FakeTransport(); var loads = 0; var liveFactories = 0;
            var transport = LiveGate.ChooseTransport(LiveArgs(), _ => null, _ => { loads++; return Fixture.LiveCredential; },
                key => { Check.That(key == Fixture.LiveCredential); liveFactories++; return fake.SendAsync; }, new FakeTransport(FakeMode.Throw).SendAsync);
            using var host = new SmokeHost(transport); await host.RunAsync(true); await host.RunAsync(false);
            Check.That(loads == 1 && liveFactories == 1 && fake.Entries == 2);
        });
        foreach (var (arguments, failure) in new[]
        {
            (new[] { "smoke-live" }, Failure.OptIn), (new[] { "--live" }, Failure.OptIn),
            (new[] { "all", "--live" }, Failure.OptIn), (new[] { "smoke-offline", "--live" }, Failure.OptIn),
            (new[] { "smoke-live", "--live" }, Failure.CredentialSource),
            (LiveArgs().Concat(new[] { "--live" }).ToArray(), Failure.Arguments),
            (LiveArgs().Concat(new[] { "--profile", "other" }).ToArray(), Failure.Profile),
        })
            await Case("opt-in-" + cases, async () =>
            {
                var loads = 0; var liveFactories = 0; var fake = new FakeTransport();
                await Fails(failure, () => Task.FromResult(LiveGate.ChooseTransport(arguments, _ => null,
                    _ => { loads++; return Fixture.LiveCredential; }, _ => { liveFactories++; return fake.SendAsync; }, fake.SendAsync)));
                Check.That(loads == 0 && liveFactories == 0 && fake.Entries == 0);
            });
        foreach (var variable in new[] { "CI", "GITHUB_ACTIONS" })
            await Case("ci-gate-" + variable, async () =>
            {
                await Fails(Failure.Ci, () => Task.FromResult(LiveGate.Select(LiveArgs(), name => name == variable ? "true" : null)));
            });
        await Case("missing-key-no-transport", async () =>
        {
            var factories = 0; var fake = new FakeTransport();
            await Fails(Failure.CredentialSource, () => Task.FromResult(LiveGate.ChooseTransport(LiveArgs(), _ => null,
                LiveGate.ReadCredential, _ => { factories++; return fake.SendAsync; }, fake.SendAsync)));
            Check.That(factories == 0 && fake.Entries == 0);
        });
        await Case("real-credential-source-admission", () => { CredentialSourceChecks.Run(); return Task.CompletedTask; });
        foreach (var bytes in new byte[][] { [], [0], [0xff], Encoding.UTF8.GetBytes("\n"), Encoding.UTF8.GetBytes("has space"), new byte[4097] })
            await Case("invalid-key-" + cases, async () =>
            {
                var factories = 0; var fake = new FakeTransport();
                await Fails(Failure.Credential, () => Task.FromResult(LiveGate.ChooseTransport(LiveArgs(), _ => null,
                    _ => LiveGate.ValidateCredential(bytes), _ => { factories++; return fake.SendAsync; }, fake.SendAsync)));
                Check.That(factories == 0 && fake.Entries == 0);
            });
        cases += await FailureChecks.RunAsync();
        cases += await WireChecks.RunAsync();
        cases += await WireObservationChecks.RunAsync();
        await Case("observer-failure-preserves-real-tool-restoration", async () =>
        {
            var fake = new FakeTransport(); using var host = new SmokeHost(fake.SendAsync);
            var first = await host.RunAsync(true, observerFailure: true);
            var second = await host.RunAsync(false, observerFailure: true);
            Check.That(first.Wire == new WireObservations() && second.Wire == new WireObservations()
                && host.Sends == 2 && host.ToolEntries == 1 && host.Effects == 1 && first.Pid != second.Pid && host.Claims == 1);
        });
        Check.That(cases > 0);
        return cases;
    }
    private static string[] LiveArgs() => ["smoke-live", "--live", "--key-file", Path.Combine(Path.GetTempPath(), "solus-missing-" + Guid.NewGuid().ToString("N"))];
    private static async Task Fails(Failure expected, Func<Task> body)
    {
        try { await body(); }
        catch (SmokeFailure failure) when (failure.Code == expected) { return; }
        throw new SmokeFailure(expected);
    }
}
