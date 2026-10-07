using System.Xml.Linq;
using Xunit;

namespace SolusAgent.ContractTests.Architecture;

/// <summary>Architecture tests over the evaluated production project graph and the sole test project registration.</summary>
public sealed class ProductionProjectGraphTests
{
    private static readonly IReadOnlyList<string> ProductionProjectNames =
    [
        "SolusAgent.Api",
        "SolusAgent.Tools.Api",
        "SolusAgent.Runtime.Api",
        "SolusAgent.Runtime",
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

    [Fact]
    public void SolutionRegistersTheFourProductionProjectsAndTheSoleTestProject()
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
            .Append(RepositoryLayout.TestProjectPath)
            .Select(ProjectBoundaries.Canonicalize)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedPaths, registeredPaths);

        var registeredTestProjects = registeredPaths
            .Where(path => ProjectBoundaries.IsWithinRoot(path, Path.Combine(RepositoryLayout.Root, "tests")))
            .ToArray();
        Assert.Single(registeredTestProjects);
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
    public void TestProjectEvaluatesOnlyCurrentTestPackagesAndNoProductionProjectReferences()
    {
        var evaluation = MsbuildProjectEvaluation.Evaluate(RepositoryLayout.TestProjectPath);

        Assert.Empty(evaluation.ProjectReferenceItems);
        ProjectBoundaryAssertions.AssertManagedNet10(evaluation);
        Assert.NotEmpty(evaluation.CompileItems);
        Assert.Equal(
            ExpectedTestPackageIds,
            evaluation.PackageIds.OrderBy(id => id, StringComparer.Ordinal).ToArray());

        var runnerPackage = evaluation.PackageReferenceItems.Single(item =>
            string.Equals(item["Identity"], "xunit.runner.visualstudio", StringComparison.Ordinal));
        Assert.Equal("all", runnerPackage.TryGetValue("PrivateAssets", out var privateAssets) ? privateAssets : string.Empty);
    }
}
