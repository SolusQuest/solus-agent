using System.Xml.Linq;
using Xunit;

namespace SolusAgent.ContractTests.Architecture;

/// <summary>
/// Proves the shared boundary assertions detect forbidden edges in a synthetic Api-only consumer.
/// Each mutation is first observed in the evaluated items, then the same assertion that passes on
/// the valid consumer and the real production projects must throw for the specific offending edge.
/// Every case runs both with plain fixture paths and with legal XML-special path characters, so the
/// generated project XML and real MSBuild evaluation are proven for both path classes.
/// </summary>
public sealed class SyntheticConsumerBoundaryTests
{
    private static readonly IReadOnlyList<string> OuterApiProjectPaths =
    [
        RepositoryLayout.ProductionProjectPath("SolusAgent.Api"),
        RepositoryLayout.ProductionProjectPath("SolusAgent.Tools.Api"),
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidApiOnlyConsumerEvaluatesInsideItsBoundary(bool useXmlSpecialPathCharacters)
    {
        using var workspace = CreateWorkspace(useXmlSpecialPathCharacters);
        var evaluation = MsbuildProjectEvaluation.Evaluate(workspace.ConsumerProjectPath);

        Assert.NotEmpty(evaluation.CompileItems);
        Assert.Contains(
            evaluation.CompileItemPaths,
            path => string.Equals(path, ProjectBoundaries.Canonicalize(workspace.LinkedSharedPath), ProjectBoundaries.PathComparison));
        Assert.Contains(
            evaluation.CompileItemPaths,
            path => string.Equals(path, ProjectBoundaries.Canonicalize(workspace.BusinessLogicPath), ProjectBoundaries.PathComparison));
        Assert.Equal(
            new[] { ProjectBoundaries.Canonicalize(RepositoryLayout.ProductionProjectPath("SolusAgent.Api")) },
            evaluation.ProjectReferencePaths);

        ProjectBoundaryAssertions.AssertReferencesWithinAllowed(evaluation, OuterApiProjectPaths);
        ProjectBoundaryAssertions.AssertCompileSourcesWithinRoot(evaluation, workspace.WorkspaceRoot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForbiddenRuntimeEdgeFailsTheSameBoundaryAssertionForTheRuntimeEdge(bool useXmlSpecialPathCharacters)
    {
        using var workspace = CreateWorkspace(useXmlSpecialPathCharacters);
        var baseline = MsbuildProjectEvaluation.Evaluate(workspace.ConsumerProjectPath);
        ProjectBoundaryAssertions.AssertReferencesWithinAllowed(baseline, OuterApiProjectPaths);

        workspace.AddDirectProjectReference(RepositoryLayout.ProductionProjectPath("SolusAgent.Runtime"));
        var mutated = MsbuildProjectEvaluation.Evaluate(workspace.ConsumerProjectPath);

        Assert.NotEmpty(mutated.ProjectReferenceItems);
        Assert.Contains(
            mutated.ProjectReferencePaths,
            path => path.EndsWith("SolusAgent.Runtime.csproj", ProjectBoundaries.PathComparison));

        var exception = Assert.Throws<BoundaryViolationException>(
            () => ProjectBoundaryAssertions.AssertReferencesWithinAllowed(mutated, OuterApiProjectPaths));
        Assert.Contains("SolusAgent.Runtime.csproj", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForbiddenRuntimeApiEdgeThroughAnImportedProjectFileFailsTheSameBoundaryAssertionForTheRuntimeApiEdge(bool useXmlSpecialPathCharacters)
    {
        using var workspace = CreateWorkspace(useXmlSpecialPathCharacters);
        var baseline = MsbuildProjectEvaluation.Evaluate(workspace.ConsumerProjectPath);
        ProjectBoundaryAssertions.AssertReferencesWithinAllowed(baseline, OuterApiProjectPaths);
        var consumerProjectTextBeforeMutation = File.ReadAllText(workspace.ConsumerProjectPath);

        workspace.AddImportedProjectReference(RepositoryLayout.ProductionProjectPath("SolusAgent.Runtime.Api"));
        var mutated = MsbuildProjectEvaluation.Evaluate(workspace.ConsumerProjectPath);

        Assert.Equal(consumerProjectTextBeforeMutation, File.ReadAllText(workspace.ConsumerProjectPath));
        Assert.NotEmpty(mutated.ProjectReferenceItems);
        Assert.Contains(
            mutated.ProjectReferencePaths,
            path => path.EndsWith("SolusAgent.Runtime.Api.csproj", ProjectBoundaries.PathComparison));

        var exception = Assert.Throws<BoundaryViolationException>(
            () => ProjectBoundaryAssertions.AssertReferencesWithinAllowed(mutated, OuterApiProjectPaths));
        Assert.Contains("SolusAgent.Runtime.Api.csproj", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinkedDownstreamSourceOutsideTheAllowedRootFailsTheSameCompileContainmentCheck(bool useXmlSpecialPathCharacters)
    {
        using var workspace = CreateWorkspace(useXmlSpecialPathCharacters);
        var baseline = MsbuildProjectEvaluation.Evaluate(workspace.ConsumerProjectPath);
        ProjectBoundaryAssertions.AssertCompileSourcesWithinRoot(baseline, workspace.WorkspaceRoot);

        workspace.AddLinkedOutsideCompileItem();
        var mutated = MsbuildProjectEvaluation.Evaluate(workspace.ConsumerProjectPath);

        var outsideCompileItem = Assert.Single(
            mutated.CompileItems,
            item => MetadataValue(item, "Identity").EndsWith("Outside.cs", StringComparison.Ordinal));
        Assert.Equal("Downstream/Outside.cs", MetadataValue(outsideCompileItem, "Link"));
        Assert.NotEmpty(mutated.CompileItems);
        Assert.Contains(
            mutated.CompileItemPaths,
            path => string.Equals(path, ProjectBoundaries.Canonicalize(workspace.OutsideSourcePath), ProjectBoundaries.PathComparison));

        var exception = Assert.Throws<BoundaryViolationException>(
            () => ProjectBoundaryAssertions.AssertCompileSourcesWithinRoot(mutated, workspace.WorkspaceRoot));
        Assert.Contains("Outside.cs", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContainmentRejectsSiblingDirectoriesSharingTheRootPathPrefix(bool useXmlSpecialPathCharacters)
    {
        using var workspace = CreateWorkspace(useXmlSpecialPathCharacters);

        Assert.True(ProjectBoundaries.IsWithinRoot(workspace.LinkedSharedPath, workspace.WorkspaceRoot));
        Assert.True(ProjectBoundaries.IsWithinRoot(workspace.OutsideSourcePath, workspace.OutsideRoot));
        Assert.False(ProjectBoundaries.IsWithinRoot(workspace.OutsideSourcePath, workspace.WorkspaceRoot));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedProjectXmlRoundTripsXmlSpecialReferenceAndSourcePathsThroughRealEvaluation(bool useXmlSpecialPathCharacters)
    {
        using var workspace = CreateWorkspace(useXmlSpecialPathCharacters);
        var directSupportProjectPath = workspace.WriteSupportProject("Support.One");
        var importedSupportProjectPath = workspace.WriteSupportProject("Support.Two");

        if (useXmlSpecialPathCharacters)
        {
            Assert.Contains("&", workspace.WorkspaceRoot, StringComparison.Ordinal);
            Assert.Contains("&", directSupportProjectPath, StringComparison.Ordinal);
            Assert.Contains("&", importedSupportProjectPath, StringComparison.Ordinal);
            Assert.Contains("&", workspace.LinkedSharedPath, StringComparison.Ordinal);
        }

        workspace.AddDirectProjectReference(directSupportProjectPath);
        workspace.AddImportedProjectReference(importedSupportProjectPath);

        // Serialization round trip: every generated attribute value in both generated files parses back exactly.
        var generatedAttributeValues = ReadGeneratedAttributeValues(workspace.ConsumerProjectPath)
            .Concat(ReadGeneratedAttributeValues(workspace.ExtensionsPropsPath))
            .ToArray();
        Assert.Contains(RepositoryLayout.ProductionProjectPath("SolusAgent.Api"), generatedAttributeValues);
        Assert.Contains(directSupportProjectPath, generatedAttributeValues);
        Assert.Contains(importedSupportProjectPath, generatedAttributeValues);
        Assert.Contains("../Linked/Shared.cs", generatedAttributeValues);
        Assert.Contains("Linked/Shared.cs", generatedAttributeValues);
        Assert.Contains("Extensions.props", generatedAttributeValues);

        // Real evaluation round trip: MSBuild resolves the same exact fixture, reference, and source paths.
        var evaluation = MsbuildProjectEvaluation.Evaluate(workspace.ConsumerProjectPath);

        var expectedReferencePaths = new[]
        {
            ProjectBoundaries.Canonicalize(RepositoryLayout.ProductionProjectPath("SolusAgent.Api")),
            ProjectBoundaries.Canonicalize(directSupportProjectPath),
            ProjectBoundaries.Canonicalize(importedSupportProjectPath),
        };
        Assert.Equal(
            expectedReferencePaths.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            evaluation.ProjectReferencePaths.OrderBy(path => path, StringComparer.Ordinal).ToArray());
        Assert.Contains(
            evaluation.CompileItemPaths,
            path => string.Equals(path, ProjectBoundaries.Canonicalize(workspace.LinkedSharedPath), ProjectBoundaries.PathComparison));
        Assert.Contains(
            evaluation.CompileItemPaths,
            path => string.Equals(path, ProjectBoundaries.Canonicalize(workspace.BusinessLogicPath), ProjectBoundaries.PathComparison));

        ProjectBoundaryAssertions.AssertReferencesWithinAllowed(evaluation, expectedReferencePaths);
        ProjectBoundaryAssertions.AssertCompileSourcesWithinRoot(evaluation, workspace.WorkspaceRoot);
    }

    private static SyntheticConsumerWorkspace CreateWorkspace(bool useXmlSpecialPathCharacters) =>
        new(useXmlSpecialPathCharacters ? SyntheticConsumerWorkspace.XmlSpecialPathToken : null);

    private static IReadOnlyList<string> ReadGeneratedAttributeValues(string projectPath) =>
        XDocument.Load(projectPath)
            .Descendants()
            .Attributes()
            .Select(attribute => attribute.Value)
            .ToArray();

    private static string MetadataValue(IReadOnlyDictionary<string, string> item, string name) =>
        item.TryGetValue(name, out var value) ? value : string.Empty;
}
