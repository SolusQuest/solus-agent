using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace SolusAgent.ContractTests.Architecture;

public sealed class PackageConsumerBoundaryTests
{
    private static string ConsumerRoot => Path.Combine(RepositoryLayout.Root, "tests", "PackageConsumers");
    private static readonly IReadOnlyDictionary<string, string[]> FoundationPackages = new Dictionary<string, string[]>
    {
        ["Business"] = ["SolusAgent.Api"],
        ["CustomTool"] = ["SolusAgent.Tools.Api"],
        ["CustomProvider"] = ["SolusAgent.Runtime.Api"],
        ["Startup"] = ["SolusAgent.Providers.DeepSeek", "SolusAgent.Runtime"],
    };

    [Fact]
    public void PackageScenariosAreNonPackableOwnSourceConsumersOutsideTheSourceSolution()
    {
        var solutionProjects = XDocument.Load(RepositoryLayout.SolutionPath).Descendants("Project")
            .Select(element => Path.GetFullPath(element.Attribute("Path")!.Value, RepositoryLayout.Root)).ToArray();
        foreach (var scenario in Directory.GetDirectories(ConsumerRoot))
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(scenario, "scenario.json")));
            var projects = manifest.RootElement.GetProperty("projects").EnumerateArray()
                .Select(value => Path.GetFullPath(value.GetString()!, scenario)).Order(StringComparer.Ordinal).ToArray();
            var discovered = Directory.GetFiles(scenario, "*.csproj", SearchOption.AllDirectories)
                .Where(path => !Path.GetRelativePath(scenario, path).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or ".local"))
                .Order(StringComparer.Ordinal).ToArray();
            Assert.NotEmpty(projects);
            Assert.Equal(projects, discovered);
            var entries = manifest.RootElement.GetProperty("entrypoints").EnumerateArray().ToArray();
            Assert.NotEmpty(entries);
            Assert.All(entries, entry => Assert.Contains(Path.GetFullPath(entry.GetString()!, scenario), projects));
            foreach (var project in projects)
            {
                Assert.DoesNotContain(project, solutionProjects);
                Assert.True(ProjectBoundaries.IsWithinRoot(project, scenario));
                var evaluation = MsbuildProjectEvaluation.Evaluate(project);
                Assert.Equal("false", evaluation.PropertyValue("IsPackable"));
                Assert.NotEqual("SolusAgent.ContractTests", evaluation.PropertyValue("AssemblyName"));
                ProjectBoundaryAssertions.AssertManagedNet10(evaluation);
                ProjectBoundaryAssertions.AssertReferencesWithinAllowed(evaluation, projects);
                ProjectBoundaryAssertions.AssertCompileSourcesWithinRoot(evaluation, scenario);
                Assert.NotEmpty(evaluation.CompileItems);
                Assert.All(evaluation.PackageReferenceItems.Where(item => item["Identity"].StartsWith("SolusAgent.", StringComparison.Ordinal)),
                    item => Assert.Equal("[0.1.0-internal.1]", item["Version"]));
            }
        }
    }

    [Fact]
    public void FoundationRetainsAllFourPublicCompileBoundariesAndOneStartup()
    {
        var foundation = Path.Combine(ConsumerRoot, "Foundation");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(foundation, "scenario.json")));
        Assert.Equal(4, manifest.RootElement.GetProperty("projects").GetArrayLength());
        Assert.Equal("Startup/Startup.csproj", Assert.Single(manifest.RootElement.GetProperty("entrypoints").EnumerateArray()).GetString());
        foreach (var (name, packages) in FoundationPackages)
        {
            var project = Path.Combine(foundation, name, name + ".csproj");
            var evaluation = MsbuildProjectEvaluation.Evaluate(project);
            Assert.Equal(packages, evaluation.PackageIds.Order(StringComparer.Ordinal));
            Assert.Equal("PackageConsumers.Foundation." + name, evaluation.PropertyValue("AssemblyName"));
            ProjectBoundaryAssertions.AssertCompileSourcesWithinRoot(evaluation, Path.GetDirectoryName(project)!);
            ProjectBoundaryAssertions.AssertExactProjectReferences(evaluation, name == "Startup"
                ? FoundationPackages.Keys.Where(key => key != "Startup").Select(key => Path.Combine(foundation, key, key + ".csproj")).ToArray()
                : []);
        }
    }

    [Fact]
    public void LegacyAgentPackageSelectionRemainsExactlyFiveAlongsideTheNeutralContract()
    {
        string[] legacyNames = ["SolusAgent.Api", "SolusAgent.Tools.Api", "SolusAgent.Runtime.Api", "SolusAgent.Runtime", "SolusAgent.Providers.DeepSeek"];
        var sourceProjects = XDocument.Load(RepositoryLayout.SolutionPath).Descendants("Project")
            .Select(element => Path.GetFullPath(element.Attribute("Path")!.Value, RepositoryLayout.Root))
            .Where(path => ProjectBoundaries.IsWithinRoot(path, Path.Combine(RepositoryLayout.Root, "src"))).ToArray();
        Assert.Equal(legacyNames.Concat(["Solus.Telemetry.Contracts", "SolusAgent.Telemetry"]).Order(StringComparer.Ordinal),
            sourceProjects.Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal));
        var production = sourceProjects.Where(path => legacyNames.Contains(Path.GetFileNameWithoutExtension(path), StringComparer.Ordinal)).ToArray();
        Assert.Equal(5, production.Length);
        Assert.Equal("true", MsbuildProjectEvaluation.Evaluate(RepositoryLayout.ProductionProjectPath("Solus.Telemetry.Contracts")).PropertyValue("IsPackable"));
        foreach (var project in production)
        {
            var evaluation = MsbuildProjectEvaluation.Evaluate(project);
            Assert.Equal("true", evaluation.PropertyValue("IsPackable"));
            Assert.Equal(evaluation.ProjectName, evaluation.PropertyValue("PackageId"));
        }
        foreach (var project in XDocument.Load(RepositoryLayout.SolutionPath).Descendants("Project")
            .Select(element => Path.GetFullPath(element.Attribute("Path")!.Value, RepositoryLayout.Root))
            .Where(path => ProjectBoundaries.IsWithinRoot(path, Path.Combine(RepositoryLayout.Root, "tests"))))
            Assert.Equal("false", MsbuildProjectEvaluation.Evaluate(project).PropertyValue("IsPackable"));
    }
}
