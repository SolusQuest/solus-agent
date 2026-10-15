using System.Text.Json;

namespace PackageConsumers.LiveSmoke;

internal static class WireChecks
{
    internal static async Task<int> RunAsync()
    {
        var cases = 0;
        foreach (var (fault, failure, expected) in new (WireFault, Failure, bool?)[]
        {
            (WireFault.Malformed, Failure.Child, null),
            (WireFault.Profile, Failure.Profile, false),
            (WireFault.Input, Failure.Capacity, true),
            (WireFault.History, Failure.Authority, true),
        })
        {
            var facts = new SafeFailureFacts(); var fake = new FakeTransport();
            var host = new SmokeHost(fake.SendAsync, facts: facts);
            try
            {
                if (fault == WireFault.History) await host.RunAsync(true);
                try
                {
                    await host.RunAsync(fresh: fault != WireFault.History, wireFault: fault);
                    throw new SmokeFailure(Failure.Protocol);
                }
                catch (SmokeFailure caught) when (caught.Code == failure) { }
                catch (JsonException) when (fault == WireFault.Malformed) { }
                var before = fault == WireFault.History ? 1 : 0;
                Check.That(fake.Entries == before && host.Sends == before);
                Check.That(facts.RequestProfileMatch == expected);
            }
            finally { host.Dispose(); }
            facts.ContextRemoved = !Directory.Exists(host.DirectoryPath);
            using var observed = JsonDocument.Parse(facts.Envelope(failure, host).Split('\n')[1]["LiveSmoke facts: ".Length..]);
            var profile = observed.RootElement.GetProperty("requestProfileMatch");
            Check.That(expected is null ? profile.ValueKind == JsonValueKind.Null : profile.GetBoolean() == expected);
            Check.That(observed.RootElement.GetProperty("contextRemoved").GetBoolean());
            Check.That(Fixture.Confined(facts.Envelope(failure, host)));
            cases++;
        }
        return cases;
    }
}
