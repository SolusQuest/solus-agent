using System.Net;
using System.Text.Json;

namespace PackageConsumers.LiveSmoke;

internal static class FailureChecks
{
    internal static async Task<int> RunAsync()
    {
        var cases = 0;
        foreach (var mode in new[] { FakeMode.RateLimited, FakeMode.Unavailable, FakeMode.Throw, FakeMode.UnknownUsage, FakeMode.EarlyFinal, FakeMode.UsageConflict, FakeMode.UnknownRoot, FakeMode.NonemptyArguments, FakeMode.MalformedArguments })
        {
            var output = new StringWriter(); var error = new StringWriter();
            var fake = new FakeTransport(mode);
            var status = await Supervisor.RunAsync(Arguments(), _ => fake.SendAsync, output, error, _ => null);
            using var json = Facts(status, output, error);
            var facts = json.RootElement;
            Check.That(facts.GetProperty("stage").GetString() == "producer"
                && facts.GetProperty("dispatches").GetInt32() == 1 && facts.GetProperty("toolEntries").GetInt32() == (mode == FakeMode.UnknownUsage ? 1 : 0)
                && facts.GetProperty("toolEffects").GetInt32() == (mode == FakeMode.UnknownUsage ? 1 : 0) && facts.GetProperty("contextRemoved").GetBoolean());
            if (mode is FakeMode.Throw or FakeMode.UnknownUsage)
                Check.That(facts.GetProperty("currentInputTokens").ValueKind == JsonValueKind.Null
                    && facts.GetProperty("currentOutputTokens").ValueKind == JsonValueKind.Null);
            if (mode is FakeMode.RateLimited or FakeMode.Unavailable)
            {
                Check.That(facts.GetProperty("httpStatus").GetInt32() == (mode == FakeMode.RateLimited ? 429 : 503)
                    && facts.GetProperty("replyMatch").ValueKind == JsonValueKind.Null);
            }
            if (mode == FakeMode.Throw) Check.That(facts.GetProperty("wireJsonValid").ValueKind == JsonValueKind.Null);
            else
            {
                Check.That(facts.GetProperty("wireJsonValid").GetBoolean());
                if (mode == FakeMode.UnknownUsage) Check.That(facts.GetProperty("wireUsageValid").ValueKind == JsonValueKind.Null);
                else Check.That(facts.GetProperty("wireUsageValid").GetBoolean() == (mode != FakeMode.UsageConflict));
                if (mode == FakeMode.UnknownRoot) Check.That(facts.GetProperty("wireShape").GetInt32() == 1);
                if (mode is FakeMode.NonemptyArguments or FakeMode.MalformedArguments)
                    Check.That(facts.GetProperty("wireArgumentsError").GetInt32() == (mode == FakeMode.NonemptyArguments ? 6 : 4));
            }
            cases++;
        }
        // Failure during restoration keeps current-run totals distinct from inherited history.
        {
            var output = new StringWriter(); var error = new StringWriter();
            var fake = new FakeTransport();
            var status = await Supervisor.RunAsync(Arguments(), _ => (body, fresh, token) => fresh
                ? fake.SendAsync(body, fresh, token) : new FakeTransport(FakeMode.UnknownUsage).SendAsync(body, fresh, token),
                output, error, _ => null);
            using var json = Facts(status, output, error);
            Check.That(json.RootElement.GetProperty("stage").GetString() == "restore"
                && json.RootElement.GetProperty("dispatches").GetInt32() == 2
                && json.RootElement.GetProperty("toolEffects").GetInt32() == 1
                && json.RootElement.GetProperty("currentInputTokens").ValueKind == JsonValueKind.Null
                && json.RootElement.GetProperty("historicalInputTokens").GetInt64() == 10);
            cases++;
        }
        // Exercise the actual HTTP admission path, preserving the status before invalid headers fail closed.
        {
            var output = new StringWriter(); var error = new StringWriter();
            var status = await Supervisor.RunAsync(Arguments(), facts => Network.Create(Fixture.LiveCredential,
                facts.ObserveStatus, () => new BadHeaders()), output, error, _ => null);
            using var json = Facts(status, output, error);
            Check.That(json.RootElement.GetProperty("httpStatus").GetInt32() == 503
                && json.RootElement.GetProperty("dispatches").GetInt32() == 1
                && json.RootElement.GetProperty("providerError").ValueKind == JsonValueKind.Null
                && json.RootElement.GetProperty("currentInputTokens").ValueKind == JsonValueKind.Null);
            cases++;
        }
        return cases;
    }
    private static string[] Arguments() => ["smoke-live", "--live", "--key-file", Path.Combine(Path.GetTempPath(), "unused-synthetic-source")];
    private static JsonDocument Facts(int status, StringWriter output, StringWriter error)
    {
        Check.That(status == 1 && output.ToString() == "" && Fixture.Confined(error.ToString()));
        var lines = error.ToString().TrimEnd('\r', '\n').Split('\n');
        Check.That(lines.Length == 2 && lines[0].StartsWith("LiveSmoke failed: ", StringComparison.Ordinal)
            && lines[1].StartsWith("LiveSmoke facts: ", StringComparison.Ordinal));
        return JsonDocument.Parse(lines[1]["LiveSmoke facts: ".Length..]);
    }
    private sealed class BadHeaders : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            { Content = new StringContent(Fixture.PrivateError + Fixture.CredentialA + Fixture.Replay) });
    }
}
