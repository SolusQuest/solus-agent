using System.Diagnostics;
using System.Text.Json;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.ScribeHost;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Providers;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Consumption;

public sealed class M4ScribeFreshProcessTests
{
    [Theory]
    [InlineData("failure", CandidateStopReason.ProductionFailed)]
    [InlineData("work", CandidateStopReason.WorkUnitLimit)]
    [InlineData("dispatch", CandidateStopReason.RuntimeLimit)]
    [InlineData("token", CandidateStopReason.RuntimeLimit)]
    [InlineData("reservation", CandidateStopReason.RuntimeLimit)]
    [InlineData("duration", CandidateStopReason.DurationLimit)]
    [InlineData("repair", CandidateStopReason.ProductionFailed)]
    [InlineData("held", CandidateStopReason.Cancelled)]
    [InlineData("capture", CandidateStopReason.ProductionFailed)]
    public async Task NewProcessReconstructsOnlyValidatedBusinessProgress(string scenario, CandidateStopReason stop)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "scribe-fresh-fixtures", Guid.NewGuid().ToString("N"));
        var a = Path.Combine(root, "A"); var b = Path.Combine(root, "B");
        Directory.CreateDirectory(b);
        var before = await Run(a, scenario + ":A");
        Assert.Equal(stop, before.Stop); Assert.Equal(1, before.BusinessCommits);
        Assert.Equal(scenario == "held" ? 0 : 1, before.Accepted);
        Assert.Equal(scenario == "repair" ? 1 : 0, before.Repairs);
        Assert.Equal(scenario == "held", before.FrozenLateFeedback);
        Assert.True(before.InputTokens >= 5);
        Assert.Equal(new[] { "alpha" }, File.ReadAllLines(Path.Combine(a, "commits.txt")));
        var product = JsonSerializer.Deserialize<ScribeProgressTransfer>(File.ReadAllText(Path.Combine(a, "business.json")))!;
        Assert.Equal("alpha", Assert.Single(product.Facts).Member);
        Assert.Equal(ScribeFreshFixture.Fact, product.Facts[0].Text);
        // The only A artifact transported to B is the validated product carrier.
        File.Copy(Path.Combine(a, "business.json"), Path.Combine(b, "business.json"));
        if (scenario == "capture")
        {
            Assert.Equal(ContextCaptureStatus.Delivered, before.Capture); Assert.Equal(1, before.Captures);
            Assert.NotEmpty(File.ReadAllBytes(Path.Combine(a, "discarded-context.bin")));
        }
        // A missing or poisoned unrelated runtime file cannot become a hidden dependency.
        File.WriteAllText(Path.Combine(b, "runtime-context.bin"), "invalid " + ScribeFreshFixture.Credential);
        var after = await Run(b, scenario + ":B");
        Assert.NotEqual(before.Pid, after.Pid); Assert.NotEqual(before.Execution, after.Execution);
        Assert.Empty(before.Logical.Intersect(after.Logical)); Assert.Empty(before.Physical.Intersect(after.Physical));
        Assert.Empty(before.Submissions.Intersect(after.Submissions));
        Assert.Equal(CandidateStopReason.Completed, after.Stop); Assert.Equal(2, after.Accepted);
        Assert.Equal(2, after.BusinessCommits); Assert.Equal(2, after.Attempts); Assert.Equal(10, after.InputTokens);
        Assert.Equal(0, after.HistoricalReceipts); Assert.Equal(0, after.HistoricalRounds);
        Assert.True(after.InitialInputsExact); Assert.True(after.NoPredecessor); Assert.True(after.NoRepairBacklink);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(b, "provider-effects.txt")).Length);
        Assert.Equal(0, after.Repairs); Assert.Equal(TimeSpan.FromSeconds(25).Ticks, after.ElapsedTicks);
        Assert.Equal(0, after.FirstMeasured); Assert.Equal(5, after.FirstReserved);
        Assert.Equal(new[] { "beta", "gamma" }, File.ReadAllLines(Path.Combine(b, "commits.txt")));
        var completed = JsonSerializer.Deserialize<ScribeProgressTransfer>(File.ReadAllText(Path.Combine(b, "business.json")))!;
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, completed.Facts.Select(f => f.Member));
        Assert.Equal("derived from " + product.Facts[0].Text, completed.Facts[1].Text);
        Assert.Equal(scenario == "capture" ? ContextCaptureStatus.Delivered : ContextCaptureStatus.NotRequested, after.Capture);
        foreach (var canary in ScribeFreshFixture.Restricted.Append(ScribeFreshFixture.Fact))
            Assert.DoesNotContain(canary, before.Safe + after.Safe);
    }

    [Theory]
    [InlineData("first accepted fact")]
    [InlineData("changed accepted fact")]
    public async Task ChangedValidatedFactCausallyChangesFreshProduction(string fact)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "scribe-fresh-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "business.json"), JsonSerializer.Serialize(new ScribeProgressTransfer(
            ["alpha", "beta", "gamma"], [new("alpha", fact)])));
        var result = await Run(root, "failure:B");
        Assert.Equal(CandidateStopReason.Completed, result.Stop);
        var product = JsonSerializer.Deserialize<ScribeProgressTransfer>(File.ReadAllText(Path.Combine(root, "business.json")))!;
        Assert.Equal("derived from " + fact, product.Facts[1].Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureIsOptionalAndOrdinaryHooklessCandidateRemainsUsable(bool capture)
    {
        var host = ScribeFreshFixture.Host(new(["alpha"], []), [new(true, continuation: CandidateContinuation.End)]);
        var provider = new ScribeFreshProvider((r, _, _) => ScribeFreshFixture.Final(r, "alpha", "fresh fact"));
        var agent = ScribeFreshFixture.Agent(provider, null);
        var sink = new RestrictedContextHost();
        await Assert.ThrowsAsync<NotSupportedException>(() => host.ExecuteFreshCandidatesWithContextAsync(agent,
            capture ? sink : null).AsTask());
        Assert.Empty(provider.Requests); Assert.Empty(host.Exchanges); Assert.Equal(0, sink.CaptureCount);
        var result = await host.ExecuteFreshCandidatesAsync(agent);
        Assert.Equal(CandidateStopReason.Completed, result.StopReason); Assert.Single(provider.Requests);
        Assert.Single(host.Progress.AcceptedFacts); Assert.Equal(0, host.ExternalEffects);
        Assert.Empty(result.HistoricalReceipts); Assert.Equal(0, sink.CaptureCount);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unselected")]
    [InlineData("malformed")]
    public async Task ReconstructedProductProgressRejectsInvalidFactsBeforeEffects(string scenario)
    {
        var facts = scenario switch
        {
            "duplicate" => new[] { new ScribeFactTransfer("alpha", "a"), new ScribeFactTransfer("alpha", "b") },
            "unselected" => [new("foreign", "a")],
            _ => [new("alpha", "invalid\u0001text")],
        };
        var root = Path.Combine(AppContext.BaseDirectory, "scribe-fresh-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "business.json"), JsonSerializer.Serialize(new ScribeProgressTransfer(["alpha"], facts)));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(M4ScribeFreshProcessTests).Assembly.Location);
        start.ArgumentList.Add("scribe-fresh-child"); start.ArgumentList.Add(root); start.ArgumentList.Add("failure:B");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotEqual(0, process.ExitCode); Assert.Contains("ArgumentException", await error);
        Assert.Empty(await output);
        Assert.False(File.Exists(Path.Combine(root, "provider-effects.txt")));
        Assert.False(File.Exists(Path.Combine(root, "commits.txt")));
        Assert.False(File.Exists(Path.Combine(root, "observation.json")));
    }

    [Fact]
    public async Task CurrentPermissionRejectsBeforeDispatchDespiteInstructionLikeBusinessFact()
    {
        var host = ScribeFreshFixture.Host(new(["alpha", "beta"], [new("alpha", ScribeFreshFixture.Fact)]), []);
        var provider = new ScribeFreshProvider((r, _, _) => ScribeFreshFixture.Final(r, "beta", "a"));
        var hooks = new RuntimeHooks { Before = (e, _) => ValueTask.FromResult<SolusAgent.Runtime.Api.Exposure.ExposureAcknowledgement?>(
            new(e, SolusAgent.Runtime.Api.Exposure.RuntimeHookStatus.Acknowledged,
                SolusAgent.Runtime.Api.Exposure.ExposureDecision.Deny, e.RequiredAcknowledgement)) };
        var result = await host.ExecuteFreshCandidatesWithContextAsync(ScribeFreshFixture.Agent(provider, hooks));
        Assert.Empty(provider.Requests); Assert.Empty(host.Exchanges); Assert.Single(host.Progress.AcceptedFacts);
        Assert.Equal(CandidateStopReason.ProductionStopped, result.Candidates!.StopReason);
        Assert.Equal(ContextAdmission.Fresh, result.Context.Admission);
        Assert.Equal(0, host.ExternalEffects);
    }

    private static async Task<ScribeFreshObservation> Run(string directory, string command)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(M4ScribeFreshProcessTests).Assembly.Location);
        start.ArgumentList.Add("scribe-fresh-child"); start.ArgumentList.Add(directory); start.ArgumentList.Add(command);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode == 0, $"Scribe child failed: {process.ExitCode}; {await output}; {await error}");
        foreach (var canary in ScribeFreshFixture.Restricted.Append(ScribeFreshFixture.Fact))
            Assert.DoesNotContain(canary, await output + await error);
        return JsonSerializer.Deserialize<ScribeFreshObservation>(File.ReadAllText(Path.Combine(directory, "observation.json")))!;
    }
}
