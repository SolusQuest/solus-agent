using System.Xml.Linq;
using Xunit;

namespace SolusAgent.ContractTests.Architecture;

/// <summary>Architecture tests over the evaluated production graph and current test-only project registrations.</summary>
public sealed class ProductionProjectGraphTests
{
    private static readonly IReadOnlyList<string> ProductionProjectNames =
    [
        "SolusAgent.Api",
        "SolusAgent.Tools.Api",
        "SolusAgent.Runtime.Api",
        "SolusAgent.Runtime",
        "SolusAgent.Providers.DeepSeek",
    ];

    private static readonly IReadOnlyList<string> IndependentApiProjectNames =
    [
        "SolusAgent.Api",
        "SolusAgent.Tools.Api",
    ];

    private static readonly IReadOnlyList<string> NoProjectReferences = [];

    private static readonly IReadOnlyList<string> RuntimeApiExpectedReferences =
    [
        RepositoryLayout.ProductionProjectPath("SolusAgent.Api"),
        RepositoryLayout.ProductionProjectPath("SolusAgent.Tools.Api"),
    ];

    private static readonly IReadOnlyList<string> RuntimeExpectedReferences =
    [
        RepositoryLayout.ProductionProjectPath("SolusAgent.Api"),
        RepositoryLayout.ProductionProjectPath("SolusAgent.Tools.Api"),
        RepositoryLayout.ProductionProjectPath("SolusAgent.Runtime.Api"),
    ];

    private static readonly IReadOnlyList<string> ExpectedTestPackageIds =
    [
        "Microsoft.NET.Test.Sdk",
        "xunit",
        "xunit.runner.visualstudio",
    ];

    private static readonly IReadOnlyList<string> TestOnlyProjectPaths =
    [
        Path.Combine(RepositoryLayout.Root, "tests", "SolusAgent.ApiOnlyConsumer", "SolusAgent.ApiOnlyConsumer.csproj"),
        RepositoryLayout.TestProjectPath,
        CustomToolsProjectPath,
        CustomProviderProjectPath,
        ScribeHostProjectPath,
        AprHostProjectPath,
    ];

    [Fact]
    public void SolutionRegistersTheCoreProjectsOptionalProviderAndCurrentTestOnlyProjects()
    {
        var registeredPaths = XDocument.Load(RepositoryLayout.SolutionPath)
            .Descendants("Project")
            .Select(element => element.Attribute("Path")?.Value)
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => ProjectBoundaries.CanonicalizeFromDirectory(RepositoryLayout.Root, path!))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        var expectedPaths = ProductionProjectNames
            .Select(RepositoryLayout.ProductionProjectPath)
            .Concat(TestOnlyProjectPaths)
            .Select(ProjectBoundaries.Canonicalize)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedPaths, registeredPaths);

        var registeredTestProjects = registeredPaths
            .Where(path => ProjectBoundaries.IsWithinRoot(path, Path.Combine(RepositoryLayout.Root, "tests")))
            .ToArray();
        Assert.Equal(TestOnlyProjectPaths.Count, registeredTestProjects.Length);
    }

    [Fact]
    public void IndependentApiProjectsEvaluateNoProjectReferencesNoPackagesAndManagedNet10()
    {
        foreach (var projectName in IndependentApiProjectNames)
        {
            var evaluation = MsbuildProjectEvaluation.Evaluate(RepositoryLayout.ProductionProjectPath(projectName));
            ProjectBoundaryAssertions.AssertExactProjectReferences(evaluation, NoProjectReferences);
            ProjectBoundaryAssertions.AssertNoPackages(evaluation);
            ProjectBoundaryAssertions.AssertManagedNet10(evaluation);
        }
    }

    [Fact]
    public void RuntimeApiEvaluatesExactReferencesToTheTwoIndependentApis()
    {
        var evaluation = MsbuildProjectEvaluation.Evaluate(RepositoryLayout.ProductionProjectPath("SolusAgent.Runtime.Api"));
        ProjectBoundaryAssertions.AssertExactProjectReferences(evaluation, RuntimeApiExpectedReferences);
        ProjectBoundaryAssertions.AssertManagedNet10(evaluation);
    }

    [Fact]
    public void RuntimeEvaluatesExactReferencesToTheTwoIndependentApisAndRuntimeApi()
    {
        var evaluation = MsbuildProjectEvaluation.Evaluate(RepositoryLayout.ProductionProjectPath("SolusAgent.Runtime"));
        ProjectBoundaryAssertions.AssertExactProjectReferences(evaluation, RuntimeExpectedReferences);
        ProjectBoundaryAssertions.AssertManagedNet10(evaluation);
    }

    [Fact]
    public void ProductionCompileInputsEvaluateInsideTheRepositoryRoot()
    {
        foreach (var projectName in ProductionProjectNames)
        {
            var evaluation = MsbuildProjectEvaluation.Evaluate(RepositoryLayout.ProductionProjectPath(projectName));
            ProjectBoundaryAssertions.AssertCompileSourcesWithinRoot(evaluation, RepositoryLayout.Root);
        }
    }

    [Fact]
    public void TestProjectEvaluatesOnlyCurrentTestPackagesAndRequiredConsumerReferences()
    {
        var evaluation = MsbuildProjectEvaluation.Evaluate(RepositoryLayout.TestProjectPath);

        ProjectBoundaryAssertions.AssertExactProjectReferences(evaluation,
        [
            RepositoryLayout.ProductionProjectPath("SolusAgent.Api"),
            Path.Combine(RepositoryLayout.Root, "tests", "SolusAgent.ApiOnlyConsumer", "SolusAgent.ApiOnlyConsumer.csproj"),
            RepositoryLayout.ProductionProjectPath("SolusAgent.Tools.Api"),
            CustomToolsProjectPath,
            RepositoryLayout.ProductionProjectPath("SolusAgent.Runtime.Api"),
            RepositoryLayout.ProductionProjectPath("SolusAgent.Providers.DeepSeek"),
            RepositoryLayout.ProductionProjectPath("SolusAgent.Runtime"),
            CustomProviderProjectPath,
            ScribeHostProjectPath,
            AprHostProjectPath,
        ]);
        ProjectBoundaryAssertions.AssertManagedNet10(evaluation);
        Assert.NotEmpty(evaluation.CompileItems);
        Assert.Equal(
            ExpectedTestPackageIds,
            evaluation.PackageIds.OrderBy(id => id, StringComparer.Ordinal).ToArray());

        var runnerPackage = evaluation.PackageReferenceItems.Single(item =>
            string.Equals(item["Identity"], "xunit.runner.visualstudio", StringComparison.Ordinal));
        Assert.Equal("all", runnerPackage.TryGetValue("PrivateAssets", out var privateAssets) ? privateAssets : string.Empty);
    }

    [Fact]
    public void CustomToolsProbeEvaluatesOnlyToolsApiAndRepositoryOwnedCompileInputs()
    {
        var evaluation = MsbuildProjectEvaluation.Evaluate(CustomToolsProjectPath);
        ProjectBoundaryAssertions.AssertExactProjectReferences(evaluation,
            [RepositoryLayout.ProductionProjectPath("SolusAgent.Tools.Api")]);
        ProjectBoundaryAssertions.AssertNoPackages(evaluation);
        ProjectBoundaryAssertions.AssertManagedNet10(evaluation);
        ProjectBoundaryAssertions.AssertCompileSourcesWithinRoot(evaluation, RepositoryLayout.Root);
        Assert.NotEmpty(evaluation.CompileItems);
    }

    private static string CustomToolsProjectPath =>
        Path.Combine(RepositoryLayout.Root, "tests", "ConsumerProbes", "CustomTools", "CustomTools.csproj");

    [Fact]
    public void CustomProviderProbeEvaluatesOnlyRuntimeApiAndRepositoryOwnedCompileInputs()
    {
        var evaluation = MsbuildProjectEvaluation.Evaluate(CustomProviderProjectPath);
        ProjectBoundaryAssertions.AssertExactProjectReferences(evaluation,
            [RepositoryLayout.ProductionProjectPath("SolusAgent.Runtime.Api")]);
        ProjectBoundaryAssertions.AssertNoPackages(evaluation);
        ProjectBoundaryAssertions.AssertManagedNet10(evaluation);
        ProjectBoundaryAssertions.AssertCompileSourcesWithinRoot(evaluation, RepositoryLayout.Root);
        Assert.NotEmpty(evaluation.CompileItems);
        var references = typeof(SolusAgent.ConsumerProbes.CustomProvider.DelegateProvider).Assembly.GetReferencedAssemblies();
        Assert.Contains(references, reference => reference.Name == "SolusAgent.Runtime.Api");
        Assert.DoesNotContain(references, reference => reference.Name == "SolusAgent.Runtime");
    }

    private static string CustomProviderProjectPath =>
        Path.Combine(RepositoryLayout.Root, "tests", "ConsumerProbes", "CustomProvider", "CustomProvider.csproj");

    [Fact]
    public void ScribeHostProbeEvaluatesOnlyApiWithOwnDirectoryCompileInputsAndNoForeignAssemblyReferences()
    {
        var evaluation = MsbuildProjectEvaluation.Evaluate(ScribeHostProjectPath);
        ProjectBoundaryAssertions.AssertExactProjectReferences(evaluation,
            [RepositoryLayout.ProductionProjectPath("SolusAgent.Api")]);
        ProjectBoundaryAssertions.AssertNoPackages(evaluation);
        ProjectBoundaryAssertions.AssertManagedNet10(evaluation);
        Assert.NotEmpty(evaluation.CompileItems);
        ProjectBoundaryAssertions.AssertCompileSourcesWithinRoot(evaluation, Path.GetDirectoryName(ScribeHostProjectPath)!);

        var references = typeof(SolusAgent.ConsumerProbes.ScribeHost.ScribeManifest).Assembly.GetReferencedAssemblies();
        Assert.Contains(references, reference => reference.Name == "SolusAgent.Api");
        Assert.DoesNotContain(references, reference => reference.Name == "SolusAgent.Runtime.Api");
        Assert.DoesNotContain(references, reference => reference.Name == "SolusAgent.Runtime");
        Assert.DoesNotContain(references, reference => reference.Name == "SolusAgent.Tools.Api");
        Assert.All(references, reference =>
        {
            var name = reference.Name ?? string.Empty;
            Assert.True(
                name == "SolusAgent.Api"
                || name is "netstandard" or "mscorlib"
                || name.StartsWith("System", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.", StringComparison.Ordinal),
                $"ScribeHost must not reference foreign assembly '{name}'.");
        });
    }

    private static string ScribeHostProjectPath =>
        Path.Combine(RepositoryLayout.Root, "tests", "ConsumerProbes", "ScribeHost", "ScribeHost.csproj");

    private static string AprHostProjectPath =>
        Path.Combine(RepositoryLayout.Root, "tests", "ConsumerProbes", "AprHost", "AprHost.csproj");
}
