using Xunit;

namespace SolusAgent.ContractTests.Architecture;

/// <summary>
/// Proves the shared boundary assertions detect forbidden edges in a synthetic Api-only consumer.
/// Each mutation is first observed in the evaluated items, then the same assertion that passes on
/// the valid consumer and the real production projects must throw for the specific offending edge.
/// </summary>
public sealed class SyntheticConsumerBoundaryTests
{
    private static readonly IReadOnlyList<string> OuterApiProjectPaths =
    [
        RepositoryLayout.ProductionProjectPath("SolusAgent.Api"),
        RepositoryLayout.ProductionProjectPath("SolusAgent.Tools.Api"),
    ];

    [Fact]
    public void ValidApiOnlyConsumerEvaluatesInsideItsBoundary()
    {
        using var workspace = new SyntheticConsumerWorkspace();
        var evaluation = MsbuildProjectEvaluation.Evaluate(workspace.ConsumerProjectPath);

        Assert.NotEmpty(evaluation.CompileItems);
        Assert.Contains(evaluation.CompileItemPaths, path => Path.GetFileName(path).Equals("Shared.cs", StringComparison.Ordinal));
        Assert.Contains(evaluation.CompileItemPaths, path => Path.GetFileName(path).Equals("BusinessLogic.cs", StringComparison.Ordinal));
        Assert.Equal(
            new[] { ProjectBoundaries.Canonicalize(RepositoryLayout.ProductionProjectPath("SolusAgent.Api")) },
            evaluation.ProjectReferencePaths);

        ProjectBoundaryAssertions.AssertReferencesWithinAllowed(evaluation, OuterApiProjectPaths);
        ProjectBoundaryAssertions.AssertCompileSourcesWithinRoot(evaluation, workspace.WorkspaceRoot);
    }

    [Fact]
    public void ForbiddenRuntimeEdgeFailsTheSameBoundaryAssertionForTheRuntimeEdge()
    {
        using var workspace = new SyntheticConsumerWorkspace();
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

    [Fact]
    public void ForbiddenRuntimeApiEdgeThroughAnImportedProjectFileFailsTheSameBoundaryAssertionForTheRuntimeApiEdge()
    {
        using var workspace = new SyntheticConsumerWorkspace();
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

    [Fact]
    public void LinkedDownstreamSourceOutsideTheAllowedRootFailsTheSameCompileContainmentCheck()
    {
        using var workspace = new SyntheticConsumerWorkspace();
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

    [Fact]
    public void ContainmentRejectsSiblingDirectoriesSharingTheRootPathPrefix()
    {
        using var workspace = new SyntheticConsumerWorkspace();

        Assert.True(ProjectBoundaries.IsWithinRoot(workspace.LinkedSharedPath, workspace.WorkspaceRoot));
        Assert.True(ProjectBoundaries.IsWithinRoot(workspace.OutsideSourcePath, workspace.OutsideRoot));
        Assert.False(ProjectBoundaries.IsWithinRoot(workspace.OutsideSourcePath, workspace.WorkspaceRoot));
    }

    private static string MetadataValue(IReadOnlyDictionary<string, string> item, string name) =>
        item.TryGetValue(name, out var value) ? value : string.Empty;
}
