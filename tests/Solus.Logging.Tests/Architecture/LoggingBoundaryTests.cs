using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;
using Xunit.Sdk;

namespace Solus.Logging.Tests.Architecture;

public sealed class LoggingBoundaryTests
{
    private static string Root
    {
        get
        {
            for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
                if (File.Exists(Path.Combine(path.FullName, "Solus.Logging.slnx"))) return path.FullName;
            throw new InvalidOperationException("Repository root not found.");
        }
    }

    private static string Contract => Path.Combine(Root, "src/Solus.Telemetry.Contracts/Solus.Telemetry.Contracts.csproj");
    private static string Logging => Path.Combine(Root, "src/Solus.Logging/Solus.Logging.csproj");
    private static string Supabase => Path.Combine(Root, "src/Solus.Logging.Supabase/Solus.Logging.Supabase.csproj");
    private static string Runner => Path.Combine(Root, "tests/Solus.Logging.Tests/Solus.Logging.Tests.csproj");
    private static string[] Membership(string name) => XDocument.Load(Path.Combine(Root, name)).Descendants("Project")
        .Select(p => Path.GetFullPath(p.Attribute("Path")!.Value, Root)).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void SolutionsShareOneRealNeutralProjectAndLoggingHasOnlyItsImplementedProjects()
    {
        var logging = Membership("Solus.Logging.slnx");
        Assert.Equal(new[] { Contract, Logging, Supabase, Runner }.Order(StringComparer.Ordinal), logging);
        Assert.Contains(Contract, Membership("SolusAgent.slnx"));
        foreach (var project in logging)
        {
            using var evaluation = Evaluate(project);
            AssertBoundary(evaluation.RootElement, project, logging);
            var properties = evaluation.RootElement.GetProperty("Properties");
            Assert.Equal("net10.0", properties.GetProperty("TargetFramework").GetString());
            Assert.Equal("false", properties.GetProperty("PublishAot").GetString());
            Assert.Equal("false", properties.GetProperty("PublishTrimmed").GetString());
            Assert.Equal(project == Runner ? "false" : "true", properties.GetProperty("IsPackable").GetString());
            Assert.All(evaluation.RootElement.GetProperty("Items").GetProperty("Compile").EnumerateArray(), item =>
                Assert.True(Within(item.GetProperty("FullPath").GetString()!, Path.GetDirectoryName(project)!)));
            var references = evaluation.RootElement.GetProperty("Items").GetProperty("ProjectReference").EnumerateArray()
                .Select(item => Path.GetFullPath(item.GetProperty("Identity").GetString()!, Path.GetDirectoryName(project)!)).Order(StringComparer.Ordinal);
            Assert.Equal((project == Contract ? Array.Empty<string>() : project == Logging ? [Contract] : project == Supabase ? [Contract, Logging] : new[] { Contract, Logging, Supabase }).Order(StringComparer.Ordinal), references);
            if (project != Runner) Assert.Empty(evaluation.RootElement.GetProperty("Items").GetProperty("PackageReference").EnumerateArray());
            if (project == Contract)
            {
                Assert.Empty(evaluation.RootElement.GetProperty("Items").GetProperty("ProjectReference").EnumerateArray());
                Assert.Empty(evaluation.RootElement.GetProperty("Items").GetProperty("PackageReference").EnumerateArray());
            }
        }
        foreach (var assembly in new[] { typeof(Solus.Telemetry.Contracts.TelemetryRecord).Assembly, typeof(TelemetryDelivery).Assembly, typeof(Solus.Logging.Supabase.SupabaseTelemetrySink).Assembly })
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), n => n.Name?.StartsWith("SolusAgent.", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("imported-project")]
    [InlineData("package")]
    [InlineData("assembly")]
    public void SameEvaluatedBoundaryRejectsForbiddenAgentEdges(string kind)
    {
        var fixture = Path.Combine(Root, ".local", "logging-architecture", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            var path = Path.Combine(fixture, "Probe.csproj");
            var edge = kind switch
            {
                "package" => "<PackageReference Include=\"SolusAgent.Api\" Version=\"0.1.0-internal.1\" />",
                "assembly" => "<Reference Include=\"SolusAgent.Api\"><HintPath>synthetic/SolusAgent.Api.dll</HintPath></Reference>",
                _ => $"<ProjectReference Include=\"{Path.Combine(Root, "src/SolusAgent.Api/SolusAgent.Api.csproj")}\" />",
            };
            var content = $"<ItemGroup>{edge}</ItemGroup>";
            if (kind == "imported-project")
            {
                File.WriteAllText(Path.Combine(fixture, "edge.props"), $"<Project>{content}</Project>");
                content = "<Import Project=\"edge.props\" />";
            }
            File.WriteAllText(path, $"<Project Sdk=\"Microsoft.NET.Sdk\">{content}</Project>");
            using var evaluation = Evaluate(path);
            var failure = Assert.ThrowsAny<XunitException>(() => AssertBoundary(evaluation.RootElement, path, [path, Contract]));
            Assert.Contains("SolusAgent.Api", failure.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(fixture, recursive: true); }
    }

    private static void AssertBoundary(JsonElement evaluation, string project, IReadOnlyList<string> membership)
    {
        var items = evaluation.GetProperty("Items");
        foreach (var item in items.GetProperty("ProjectReference").EnumerateArray())
        {
            var reference = Path.GetFullPath(item.GetProperty("Identity").GetString()!, Path.GetDirectoryName(project)!);
            Assert.False(Path.GetFileNameWithoutExtension(reference).StartsWith("SolusAgent.", StringComparison.Ordinal), $"Forbidden Agent project: {reference}");
            Assert.Contains(reference, membership);
        }
        foreach (var type in new[] { "PackageReference", "Reference" })
            foreach (var item in items.GetProperty(type).EnumerateArray())
            {
                var identity = item.GetProperty("Identity").GetString()!;
                Assert.False((identity.StartsWith("SolusAgent.", StringComparison.Ordinal) || Path.GetFileNameWithoutExtension(identity).StartsWith("SolusAgent.", StringComparison.Ordinal)), $"Forbidden Agent {type}: {identity}");
                if (item.TryGetProperty("HintPath", out var hint))
                    Assert.False(Path.GetFileNameWithoutExtension(hint.GetString()!).StartsWith("SolusAgent.", StringComparison.Ordinal), $"Forbidden Agent assembly: {hint}");
            }
    }

    private static bool Within(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static JsonDocument Evaluate(string project)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "msbuild", project, "-nologo", "-property:Configuration=Release", "-getItem:ProjectReference,PackageReference,Reference,Compile", "-getProperty:TargetFramework,PublishAot,PublishTrimmed,IsPackable" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("MSBuild evaluation timed out."); }
        Assert.True(process.ExitCode == 0, error.GetAwaiter().GetResult());
        return JsonDocument.Parse(output.GetAwaiter().GetResult());
    }
}
