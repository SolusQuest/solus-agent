namespace PackageConsumers.LiveSmoke;

internal static class Supervisor
{
    // Dependency injection stays private to this installed-package fixture; the CLI always uses the real live gate.
    internal static async Task<int> RunAsync(string[] args, Func<SafeFailureFacts, Send>? choose = null,
        TextWriter? output = null, TextWriter? error = null, Func<string, string?>? environment = null)
    {
        output ??= Console.Out; error ??= Console.Error; environment ??= Environment.GetEnvironmentVariable;
        var facts = new SafeFailureFacts();
        SmokeHost? host = null;
        Report? first = null, second = null;
        Failure? failed = null;
        var live = false;
        try
        {
            if (args is ["--child"])
                return await Child.RunAsync();
            var selection = LiveGate.Select(args, name => environment(name));
            live = selection.Live;
            if (live) facts.Credential();
            var transport = choose is null ? LiveGate.ChooseTransport(args, environment,
                LiveGate.ReadCredential, key => Network.Create(key, facts.ObserveStatus), new FakeTransport().SendAsync) : choose(facts);
            if (!live)
            {
                var cases = await SelfChecks.RunAsync();
                output.WriteLine($"LiveSmoke offline passed: {cases} behavioral cases; controlled transport only; fresh-process restoration executed.");
            }
            else
            {
                host = new SmokeHost(transport, duration: TimeSpan.FromMinutes(5) - facts.Elapsed, facts: facts);
                first = await host.RunAsync(fresh: true);
                second = await host.RunAsync(fresh: false);
                Check.That(first.Pid != second.Pid && host.Sends == 2 && host.ToolEntries == 1 && host.Effects == 1);
            }
        }
        catch (SmokeFailure failure) { failed = failure.Code; }
        catch { failed = Failure.Child; }
        finally
        {
            if (host is not null)
            {
                try
                {
                    host.Dispose();
                    facts.ContextRemoved = !Directory.Exists(host.DirectoryPath);
                    if (facts.ContextRemoved != true) failed = Failure.Confinement;
                }
                catch { facts.ContextRemoved = false; failed = Failure.Confinement; }
            }
        }
        if (failed is { } failureCode)
        {
            error.WriteLine(facts.Envelope(failureCode, host));
            return 1;
        }
        if (live)
        {
            try
            {
                output.WriteLine("LiveSmoke live passed: " + SafeSummary.Create(first!, second!, host!.Sends, host.ToolEntries,
                    host.Effects, facts.Elapsed, facts.ContextRemoved == true));
            }
            catch (SmokeFailure failure) { error.WriteLine(facts.Envelope(failure.Code, host)); return 1; }
            catch { error.WriteLine(facts.Envelope(Failure.Child, host)); return 1; }
        }
        return 0;
    }
}
