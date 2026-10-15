using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using PackageConsumers.Apr.Business;
using PackageConsumers.Apr.CustomTool;
using PackageConsumers.Apr.Startup;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Usage;

try
{
    if (args.Length == 4 && args[0] == "child")
    {
        await ChildScenario.RunAsync(args[1], args[2], args[3]);
        return 0;
    }
    Check.That(args.Length == 0, "Unexpected scenario arguments.");
    CheckReferences(typeof(AprBusinessHost).Assembly, "SolusAgent.Api");
    CheckReferences(typeof(IncrementTool).Assembly, "SolusAgent.Tools.Api");
    var root = Path.Combine(Environment.CurrentDirectory, ".local", "apr", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var cases = new List<string>(); var children = new List<int>();

    var mixedRoot = Path.Combine(root, "mixed");
    var a = await Run(mixedRoot, "mixed", "produce");
    Check.That(a.Admission == ContextAdmission.Fresh && a.Stop == CandidateStopReason.WorkUnitLimit && a.Sends == 3
        && a.ToolEntries == 1 && a.ToolEffects == 1 && a.Hosts == 2 && a.CurrentRepairs == 1 && a.CurrentContinuations == 1
        && a.AcceptedCount == 1 && a.Accepted.Single().Item == "A" && a.HostEffects == 1 && a.CurrentTools == 1
        && a.CurrentAttempts == 3 && a.Input == 30 && a.Output == 15 && a.CampaignAttempts == 3 && a.CampaignCharge == 30,
        "Producer did not stop after acceptance, rejection and completed tool work.");
    var b = await Run(mixedRoot, "mixed", "restore");
    Check.That(b.Admission == ContextAdmission.Supplied && b.Stop == CandidateStopReason.Completed && b.Imports == 1 && b.Claims == 1
        && b.Sends == 1 && b.ToolEntries == 0 && b.ToolEffects == 1 && b.CurrentTools == 0 && b.HistoricalTools == 1
        && b.Hosts == 1 && b.NewHostEffects == 1 && b.HostEffects == 2 && b.AcceptedCount == 2
        && b.Accepted.First() == a.Accepted.Single() && b.RepairsSubmission == a.RejectedSubmission
        && b.HistoricalReceipts == 2 && b.CurrentReceipts == 1 && b.CurrentRepairs == 0 && b.CurrentContinuations == 0
        && b.CurrentAttempts == 1 && b.HistoricalAttempts == 3 && b.Input == 10 && b.Output == 5 && b.HistoricalKnownInput == 30
        && b.InputAllowance == 64 && b.MeasuredInput == 10 && b.UnresolvedInput == 0 && b.CampaignAttempts == 4 && b.CampaignCharge == 40,
        "Restoration replayed completed work or lost candidate, usage or effect state.");
    Check.That(a.Checkpoint!.LogicalWorkId == b.Checkpoint!.LogicalWorkId && a.Checkpoint.RoundId != b.Checkpoint.RoundId
        && a.Execution != b.Execution && !a.PhysicalAttempts.Intersect(b.PhysicalAttempts).Any(), "Restored round identity did not renew.");
    var terminal = await Run(mixedRoot, "mixed", "terminal");
    Check.That(terminal.Admission == ContextAdmission.Supplied && terminal.Stop == CandidateStopReason.Completed && terminal.AcceptedCount == 2
        && terminal.Sends == 0 && terminal.Hosts == 0 && terminal.ToolEntries == 0 && terminal.HostEffects == 2
        && terminal.CurrentAttempts == 0 && terminal.HistoricalAttempts == 4 && terminal.HistoricalReceipts == 3
        && terminal.CampaignAttempts == 4 && terminal.CampaignCharge == 40, "Completed continuation repeated effects or historical charges.");
    cases.Add("completed-tool-candidate-restoration");

    var retryRoot = Path.Combine(root, "retry");
    var failed = await Run(retryRoot, "retry", "produce");
    Check.That(failed.Sends == 2 && failed.AcceptedCount == 1 && failed.HostEffects == 1 && failed.CurrentAttempts == 2
        && failed.Input == 10 && failed.InputCoverage == TokenObservationCoverage.Partial && failed.UnresolvedInput == 64
        && failed.CampaignAttempts == 2 && failed.CampaignCharge == 74 && failed.Stop == CandidateStopReason.RuntimeLimit,
        "503 did not retain the accepted progress and pending retry.");
    var retried = await Run(retryRoot, "retry", "restore");
    Check.That(retried.Admission == ContextAdmission.Supplied && retried.Stop == CandidateStopReason.Completed && retried.Sends == 1
        && retried.AcceptedCount == 2 && retried.Hosts == 1 && retried.HostEffects == 2 && retried.Accepted.First() == failed.Accepted.Single()
        && retried.CurrentAttempts == 1 && retried.HistoricalAttempts == 2 && retried.Input is null && retried.Output is null
        && retried.InputCoverage == TokenObservationCoverage.Unavailable && retried.UnresolvedInput == 64 && retried.ConservativeInput == 0 && retried.UnresolvedOutput == 16 && retried.ConservativeOutput == 0
        && retried.MeasuredInput == 0 && retried.HistoricalKnownInput == 10 && retried.InputAllowance == 64
        && retried.CampaignAttempts == 3 && retried.CampaignCharge == 138 && retried.Ordinals.Single() == 2
        && retried.LogicalCalls.Single() == failed.LogicalCalls.Last() && retried.PreviousAttempt == failed.PhysicalAttempts.Last()
        && retried.PhysicalAttempts.Single() != failed.PhysicalAttempts.Last(), "Restored retry lost lineage or invented known usage.");
    cases.Add("accepted-then-503-and-unknown-usage");

    foreach (var stage in new[] { "malformed", "truncated", "tamper", "target", "duplicate", "competing" })
    {
        var directory = Path.Combine(root, stage);
        var before = await Run(directory, "mixed", "produce");
        if (stage is "duplicate" or "competing") before = await Run(directory, "mixed", "restore");
        var rejected = await Run(directory, "mixed", stage);
        Check.That(rejected.Admission == ContextAdmission.Rejected && rejected.Rejection == (stage is "malformed" or "truncated" ? ContextRejectionCode.InvalidContext : ContextRejectionCode.InvalidRunTransition)
            && !rejected.HasOutcome && rejected.Stop is null && rejected.Capture == ContextCaptureStatus.Unavailable
            && rejected.Claims == 0 && rejected.Imports == 0 && rejected.Providers == 0 && rejected.Sends == 0
            && rejected.Permissions == 0 && rejected.Closures == 0 && rejected.ToolEntries == 0 && rejected.Hosts == 0
            && rejected.Progress == 0 && rejected.Sinks == 0 && rejected.NewHostEffects == 0
            && rejected.ToolEffects == before.ToolEffects && rejected.HostEffects == before.HostEffects
            && rejected.Accepted.SequenceEqual(before.Accepted) && rejected.CampaignCharge == before.CampaignCharge,
            "Invalid state reached work, capture, import or new effects.");
        Check.That(rejected.AuthorityCalls == (stage is "malformed" or "truncated" ? 0 : 1), "Negative exercised the wrong admission gate.");
        cases.Add(stage + "-before-work");
    }

    var lowRoot = Path.Combine(root, "low-budget");
    var lowBefore = await Run(lowRoot, "mixed", "produce"); var low = await Run(lowRoot, "mixed", "low-budget");
    Check.That(low.Admission == ContextAdmission.Supplied && low.Claims == 1 && low.Imports == 1
        && low.Stop == CandidateStopReason.RuntimeLimit && low.Sends == 0 && low.CurrentAttempts == 0 && low.Input == 0
        && low.Hosts == 0 && low.ToolEntries == 0 && low.ToolEffects == 1 && low.HostEffects == 1
        && low.Accepted.Single() == lowBefore.Accepted.Single() && low.CampaignCharge == 30,
        "Current allowance was bypassed or historical usage charged again.");
    cases.Add("current-budget-before-dispatch");

    var unknownRoot = Path.Combine(root, "unknown");
    var unknownBefore = await Run(unknownRoot, "unknown", "produce"); var unknown = await Run(unknownRoot, "unknown", "restore");
    Check.That(unknownBefore.Stop == CandidateStopReason.UnknownAcknowledgement && unknownBefore.Hosts == 2
        && unknownBefore.AcceptedCount == 1 && unknownBefore.HostEffects == 1
        && unknown.Admission == ContextAdmission.Supplied && unknown.Stop == CandidateStopReason.UnknownAcknowledgement
        && unknown.Sends == 0 && unknown.Hosts == 0 && unknown.ToolEntries == 0 && unknown.CurrentAttempts == 0
        && unknown.HistoricalReceipts == 2 && unknown.Accepted.Single() == unknownBefore.Accepted.Single()
        && unknown.HostEffects == 1 && unknown.CampaignCharge == unknownBefore.CampaignCharge,
        "Unknown acknowledgement was fabricated or automatically resubmitted.");
    cases.Add("unknown-feedback-no-resubmission");

    Check.That(cases.Count == 10 && children.Count == 23 && children.Distinct().Count() == children.Count
        && children.All(pid => pid != Environment.ProcessId), "Scenario executed an unexpected case/process count.");
    Store.Write(Path.Combine(root, "summary.json"), new { Cases = cases, ChildProcesses = children.Count,
        Framework = RuntimeInformation.FrameworkDescription, Platform = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString() });
    Console.WriteLine($"APR passed: {cases.Count} cases, {children.Count} independent child processes; package-only restored execution.");
    return 0;

    async Task<Observation> Run(string directory, string scenario, string stage)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var value in new[] { typeof(Observation).Assembly.Location, "child", directory, scenario, stage }) start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch child.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw new InvalidOperationException("Child exceeded its finite timeout."); }
        var stdout = await output; var stderr = await error;
        Check.Confined(stdout + stderr);
        Check.That(process.ExitCode == 0, "Child failed: " + scenario + "/" + stage + ": " + stderr);
        Check.That(stdout.Trim() == "APR child assertions passed." && string.IsNullOrEmpty(stderr), "Child did not complete its assertions.");
        var observation = Store.Read<Observation>(Path.Combine(directory, stage + "-observation.json"));
        Check.That(observation.Pid == process.Id && observation.Pid != Environment.ProcessId, "Child PID evidence is inconsistent.");
        Check.That(observation.Capture == ContextCaptureStatus.Unavailable || observation.Capture == ContextCaptureStatus.Delivered,
            "Expected checkpoint capture failed.");
        children.Add(observation.Pid); return observation;
    }
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

static void CheckReferences(Assembly assembly, string expected)
{
    var actual = assembly.GetReferencedAssemblies().Select(reference => reference.Name!)
        .Where(name => name.StartsWith("SolusAgent.", StringComparison.Ordinal)).ToArray();
    Check.That(actual.Length == 1 && actual[0] == expected, "Independent consumer compiled against a forbidden production assembly.");
}
