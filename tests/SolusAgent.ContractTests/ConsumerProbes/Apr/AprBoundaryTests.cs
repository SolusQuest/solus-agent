using System.Xml.Linq;
using AprHost;
using SolusAgent.ContractTests.Architecture;
using Xunit;

namespace SolusAgent.ContractTests.ConsumerProbes.Apr;

/// <summary>Evaluated and compiled boundary checks for the Api-only APR Host library.</summary>
public sealed class AprBoundaryTests
{
    [Fact]
    public void AprHostEvaluatesApiOnlyNoPackagesManagedNet10AndContainedSources()
    {
        var evaluation = MsbuildProjectEvaluation.Evaluate(AprHostProjectPath);
        ProjectBoundaryAssertions.AssertExactProjectReferences(evaluation,
            [RepositoryLayout.ProductionProjectPath("SolusAgent.Api")]);
        ProjectBoundaryAssertions.AssertNoPackages(evaluation);
        ProjectBoundaryAssertions.AssertManagedNet10(evaluation);
        ProjectBoundaryAssertions.AssertCompileSourcesWithinRoot(evaluation, RepositoryLayout.Root);
        Assert.NotEmpty(evaluation.CompileItems);
    }

    [Fact]
    public void AprHostCompiledAssemblyReferencesApiOnlyWithoutRuntimeToolsOrFixtureEdges()
    {
        var references = typeof(AprBusinessHost).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();
        Assert.Contains("SolusAgent.Api", references);
        Assert.DoesNotContain("SolusAgent.Runtime", references);
        Assert.DoesNotContain("SolusAgent.Runtime.Api", references);
        Assert.DoesNotContain("SolusAgent.Tools.Api", references);
        Assert.DoesNotContain("CustomTools", references);
        Assert.DoesNotContain("CustomProvider", references);
    }

    [Fact]
    public void SolutionAndTestRunnerRegisterTheAprHostProject()
    {
        var registeredPaths = XDocument.Load(RepositoryLayout.SolutionPath)
            .Descendants("Project")
            .Select(element => element.Attribute("Path")?.Value)
            .Where(path => !string.IsNullOrEmpty(path))
            .ToArray();
        Assert.Contains("tests/ConsumerProbes/AprHost/AprHost.csproj", registeredPaths);

        var evaluation = MsbuildProjectEvaluation.Evaluate(RepositoryLayout.TestProjectPath);
        Assert.Contains(evaluation.ProjectReferencePaths,
            path => path.EndsWith("AprHost.csproj", ProjectBoundaries.PathComparison));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForbiddenRuntimeEdgeFailsTheAprAllowedBoundaryAssertion(bool useXmlSpecialPathCharacters)
    {
        using var workspace = CreateWorkspace(useXmlSpecialPathCharacters);
        var allowed = AprAllowedReferences;

        workspace.AddDirectProjectReference(RepositoryLayout.ProductionProjectPath("SolusAgent.Runtime"));
        var mutated = MsbuildProjectEvaluation.Evaluate(workspace.ConsumerProjectPath);

        var exception = Assert.Throws<BoundaryViolationException>(
            () => ProjectBoundaryAssertions.AssertReferencesWithinAllowed(mutated, allowed));
        Assert.Contains("SolusAgent.Runtime.csproj", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForbiddenRuntimeApiEdgeThroughAnImportFailsTheAprAllowedBoundaryAssertion(bool useXmlSpecialPathCharacters)
    {
        using var workspace = CreateWorkspace(useXmlSpecialPathCharacters);
        var allowed = AprAllowedReferences;

        workspace.AddImportedProjectReference(RepositoryLayout.ProductionProjectPath("SolusAgent.Runtime.Api"));
        var mutated = MsbuildProjectEvaluation.Evaluate(workspace.ConsumerProjectPath);

        var exception = Assert.Throws<BoundaryViolationException>(
            () => ProjectBoundaryAssertions.AssertReferencesWithinAllowed(mutated, allowed));
        Assert.Contains("SolusAgent.Runtime.Api.csproj", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinkedSourceOutsideTheWorkspaceFailsTheAprCompileContainmentCheck(bool useXmlSpecialPathCharacters)
    {
        using var workspace = CreateWorkspace(useXmlSpecialPathCharacters);

        workspace.AddLinkedOutsideCompileItem();
        var mutated = MsbuildProjectEvaluation.Evaluate(workspace.ConsumerProjectPath);

        var exception = Assert.Throws<BoundaryViolationException>(
            () => ProjectBoundaryAssertions.AssertCompileSourcesWithinRoot(mutated, workspace.WorkspaceRoot));
        Assert.Contains("Outside.cs", exception.Message, StringComparison.Ordinal);
    }

    private static string AprHostProjectPath =>
        Path.Combine(RepositoryLayout.Root, "tests", "ConsumerProbes", "AprHost", "AprHost.csproj");

    private static IReadOnlyList<string> AprAllowedReferences =>
        [RepositoryLayout.ProductionProjectPath("SolusAgent.Api"), AprHostProjectPath];

    private static SyntheticConsumerWorkspace CreateWorkspace(bool useXmlSpecialPathCharacters)
    {
        var workspace = new SyntheticConsumerWorkspace(useXmlSpecialPathCharacters ? SyntheticConsumerWorkspace.XmlSpecialPathToken : null);
        workspace.AddDirectProjectReference(AprHostProjectPath);
        return workspace;
    }
}
