using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Solus.Logging.Cli;

if (args.Length == 1 && args[0] == "--version")
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        protocol = 1,
        version = typeof(CliApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
        runtimeIdentifier = RuntimeInformation.RuntimeIdentifier,
    }));
    return 0;
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
using var terminate = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, e => { e.Cancel = true; cancellation.Cancel(); });
var result = await CliApplication.RunAsync(args, Console.OpenStandardInput(), Environment.GetEnvironmentVariable, cancellation.Token);
Console.WriteLine(result.Json);
return result.ExitCode;
