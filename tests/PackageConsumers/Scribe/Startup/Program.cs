using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using PackageConsumers.Scribe.Business;
using PackageConsumers.Scribe.CustomTool;
using PackageConsumers.Scribe.Startup;
using SolusAgent.Api.Candidates;

try
{
    if (args.Length == 4 && args[0] == "child") { await Child.Run(args[1], args[2] == "A", args[3]);return 0; }
    Check.That(args.Length == 0, "Unexpected arguments.");
    foreach (var pair in new[] { (typeof(ScribeHost).Assembly, "SolusAgent.Api"), (typeof(MemberTool).Assembly, "SolusAgent.Tools.Api") })
        Check.That(pair.Item1.GetReferencedAssemblies().Where(a => a.Name!.StartsWith("SolusAgent.", StringComparison.Ordinal)).Select(a => a.Name).SequenceEqual(new[] { pair.Item2 }), "Forbidden compiled consumer edge.");
    var root = Path.Combine(Environment.CurrentDirectory, ".local", "scribe", Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
    var pids = new List<int>();var cases = new List<string>();string? normalGamma = null;
    foreach (var variant in new[] { "normal", "changed", "instruction", "unicode", "repair", "unknown", "usage" })
    {
        var aDirectory = Path.Combine(root, variant, "A");var bDirectory = Path.Combine(root, variant, "B");
        var initial = new BusinessProgress(["alpha", "beta", "gamma"], []);
        var a = await Run(aDirectory, "A", variant, initial);
        var carried = Read<BusinessProgress>(aDirectory, "validated.json");
        Check.That(carried.Accepted.Length == (variant is "repair" or "unknown" ? 1 : 2)
            && a.HostEffects == carried.Accepted.Length && a.ToolEntries.SequenceEqual(new[] { "alpha" }), "Source business progress/effects were not retained.");
        var targetBefore = Directory.GetFiles(Path.Combine(aDirectory, "targets")).Select(File.ReadAllBytes).Select(Convert.ToHexString).ToArray();
        var b = await Run(bDirectory, "B", variant, carried);
        var final = Read<BusinessProgress>(bDirectory, "validated.json");
        var beforeEffects = Directory.GetFiles(Path.Combine(aDirectory, "host-effects")).Select(Path.GetFileName).ToArray();
        var newEffects = Directory.GetFiles(Path.Combine(bDirectory, "host-effects")).Select(Path.GetFileName).ToArray();
        Check.That(!beforeEffects.Intersect(newEffects).Any() && newEffects.Order().SequenceEqual(initial.Selected.Except(carried.Accepted.Select(f => f.Member)).Select(m => m + ".effect").Order()), "Completed Host effect was repeated.");
        Check.That(final.Accepted.Length == 3 && carried.Accepted.All(f => final.Accepted.Contains(f)) && b.Stop == CandidateStopReason.Completed
            && b.HostEffects == 3 - carried.Accepted.Length && b.Retained == carried.Accepted.Length && b.Accepts == b.HostEffects
            && b.ToolEntries.SequenceEqual(initial.Selected.Except(carried.Accepted.Select(f => f.Member)))
            && b.Backlinks == 0 && b.Repairs == 0 && b.History == 0 && b.Imports == 0
            && b.Sends == 1 + b.HostEffects && b.Input == b.Sends * 10 && b.Output == b.Sends * 5 && b.Unresolved == 0
            && b.InitialMeasured == 0 && b.InitialReserved == 64 && b.InitialAttempts == 1, "Fresh reconstruction repeated work or inherited accounting.");
        Check.That(a.Execution != b.Execution && !a.Physical.Intersect(b.Physical).Any() && !a.Logical.Intersect(b.Logical).Any()
            && !a.Submissions.Intersect(b.Submissions).Any(), "Fresh execution identities were inherited.");
        Check.That(targetBefore.SequenceEqual(Directory.GetFiles(Path.Combine(aDirectory, "targets")).Select(File.ReadAllBytes).Select(Convert.ToHexString)), "Earlier effect target changed during fresh B.");
        if (variant is "normal" or "changed" or "instruction" or "unicode" or "usage")
            Check.That(a.Stop == CandidateStopReason.WorkUnitLimit && a.Sends == 4 && a.Receipts == 3 && a.Accepts == 2 && a.Repairs == 1
                && a.Backlinks == 1 && a.Input == (variant == "usage" ? 30 : 40) && a.Unresolved == (variant == "usage" ? 64 : 0), "Primary repair/partial/accounting path differed.");
        if (variant == "repair") Check.That(a.Stop == CandidateStopReason.RepairLimit && a.Sends == 3 && a.Receipts == 2 && a.Accepts == 1 && a.Repairs == 0, "Denied repair dispatched dependent work.");
        if (variant == "unknown") Check.That(a.Stop == CandidateStopReason.UnknownAcknowledgement && a.Sends == 2 && a.Accepts == 0 && a.Receipts == 1 && a.HostEffects == 1, "Committed progress was confused with acknowledgement.");
        var gamma = final.Accepted.Single(f => f.Member == "gamma").Text;
        Check.That(gamma == ScribeHost.Gamma(carried.Accepted), "Gamma did not depend on actual reconstructed facts.");
        if (variant == "normal") normalGamma = gamma;
        if (variant == "changed") Check.That(gamma != normalGamma, "Changed correction did not change reconstructed result.");
        cases.Add(variant);
    }
    foreach (var negative in new[] { "duplicate", "foreign", "malformed", "control", "newline", "nul", "surrogate-high", "surrogate-low", "utf8-bound" })
    {
        var fact = new Fact("alpha", ScribeHost.Alpha("normal"), true);
        Fact[] facts = negative == "duplicate" ? [fact, fact] : negative == "foreign" ? [fact with { Member = "foreign" }] : [fact with { Text = negative switch { "control" => "invalid\u0001text", "newline" => "invalid\ntext", "nul" => "invalid\0text", "utf8-bound" => new string((char)0x00E9, 200), _ => "" } }];
        var observed = await Run(Path.Combine(root, negative), "B", "negative-" + negative, new(["alpha", "beta", "gamma"], facts));
        Check.That(observed.Rejected && observed.Sends == 0 && observed.HostEffects == 0 && observed.Imports == 0 && observed.ToolEntries.Length == 0, "Invalid business input reached work.");cases.Add(negative);
    }
    Check.That(cases.Count == 16 && pids.Count == 23 && pids.Distinct().Count() == 23, "Incorrect executed case/process count.");
    File.WriteAllText(Path.Combine(root, "summary.json"), JsonSerializer.Serialize(new { Cases = cases, Children = pids.Count }));
    Console.WriteLine("Scribe passed: 16 cases, 23 distinct child processes; fresh business reconstruction, no Runtime import.");return 0;

    async Task<Observation> Run(string directory, string stage, string variant, BusinessProgress progress)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "business.json"), JsonSerializer.SerializeToUtf8Bytes(progress));
        // B receives exactly this domain carrier; source envelopes/observations are never copied.
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
        foreach (var value in new[] { Assembly.GetExecutingAssembly().Location, "child", directory, stage, variant }) start.ArgumentList.Add(value);
        using var process = Process.Start(start)!;var stdout = process.StandardOutput.ReadToEndAsync();var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { process.Kill(true);await process.WaitForExitAsync();throw new InvalidOperationException("Owned child timed out."); }
        var output = await stdout;var error = await stderr;Check.Safe(output + error);
        Check.That(process.ExitCode == 0 && output.Trim() == "Scribe child passed." && error.Length == 0, "Child failed: " + error);
        var observed = Read<Observation>(directory, "observation.json");Check.That(observed.Pid == process.Id && observed.Pid != Environment.ProcessId, "Child PID mismatch.");pids.Add(observed.Pid);return observed;
    }
}
catch (Exception error) { Console.Error.WriteLine(error.Message);return 1; }
static T Read<T>(string directory, string name) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(Path.Combine(directory, name)))!;
