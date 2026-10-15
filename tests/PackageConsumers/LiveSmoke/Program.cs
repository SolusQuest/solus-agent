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
        using (var host = new SmokeHost(transport, duration: TimeSpan.FromMinutes(5) - elapsed.Elapsed))
        {
            var first = await host.RunAsync(fresh: true);
            var second = await host.RunAsync(fresh: false);
            Check.That(first.Pid != second.Pid && host.Sends == 2 && host.ToolEntries == 1 && host.Effects == 1);
        }
        Console.WriteLine("LiveSmoke live passed: 2 dispatches, 1 tool entry, 2 distinct processes; restricted temporary context removed.");
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
