using PackageConsumers.LiveSmoke;

try
{
    var elapsed = System.Diagnostics.Stopwatch.StartNew();
    if (args is ["--child"])
        return await Child.RunAsync();
    var selection = LiveGate.Select(args, name => Environment.GetEnvironmentVariable(name));
    var transport = LiveGate.ChooseTransport(args, name => Environment.GetEnvironmentVariable(name),
        LiveGate.ReadCredential, Network.Send, new FakeTransport().SendAsync);
    if (!selection.Live)
    {
        var cases = await SelfChecks.RunAsync();
        Console.WriteLine($"LiveSmoke offline passed: {cases} behavioral cases; controlled transport only; fresh-process restoration executed.");
    }
    else
    {
        Report first, second; string directory; int sends, tools, effects;
        using (var host = new SmokeHost(transport, duration: TimeSpan.FromMinutes(5) - elapsed.Elapsed))
        {
            first = await host.RunAsync(fresh: true);
            second = await host.RunAsync(fresh: false);
            Check.That(first.Pid != second.Pid && host.Sends == 2 && host.ToolEntries == 1 && host.Effects == 1);
            directory = host.DirectoryPath; sends = host.Sends; tools = host.ToolEntries; effects = host.Effects;
        }
        var removed = !Directory.Exists(directory); Check.That(removed, Failure.Confinement);
        Console.WriteLine("LiveSmoke live passed: " + SafeSummary.Create(first, second, sends, tools, effects, elapsed.Elapsed, removed));
    }
    return 0;
}
catch (SmokeFailure failure)
{
    Console.Error.WriteLine($"LiveSmoke failed: {failure.Code}.");
    return 1;
}
catch
{
    Console.Error.WriteLine("LiveSmoke failed: Child.");
    return 1;
}
