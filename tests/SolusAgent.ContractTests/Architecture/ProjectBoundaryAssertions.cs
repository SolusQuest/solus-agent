namespace SolusAgent.ContractTests.Architecture;

/// <summary>Thrown when a boundary assertion detects a specific offending edge.</summary>
public sealed class BoundaryViolationException(string message) : Exception(message);

/// <summary>
/// Boundary assertions shared by the production graph checks and the synthetic consumer checks.
/// The production checks and the negative mutation checks call these same methods, so a mutation
/// is proven detectable when the same assertion that passes on real projects throws here.
/// </summary>
internal static class ProjectBoundaryAssertions
{
    public static void AssertExactProjectReferences(ProjectEvaluation evaluation, IReadOnlyList<string> expectedProjectPaths)
    {
        AssertReferencesWithinAllowed(evaluation, expectedProjectPaths);

        foreach (var expected in expectedProjectPaths)
        {
            var canonicalExpected = ProjectBoundaries.Canonicalize(expected);
            if (!evaluation.ProjectReferencePaths.Any(path => string.Equals(path, canonicalExpected, ProjectBoundaries.PathComparison)))
            {
                throw new BoundaryViolationException(
                    $"Project '{evaluation.ProjectName}' is missing expected project reference '{canonicalExpected}'.");
            }
        }
    }

    public static void AssertReferencesWithinAllowed(ProjectEvaluation evaluation, IReadOnlyList<string> allowedProjectPaths)
    {
        var allowed = allowedProjectPaths.Select(ProjectBoundaries.Canonicalize).ToArray();

        foreach (var referencePath in evaluation.ProjectReferencePaths)
        {
            if (!allowed.Any(path => string.Equals(path, referencePath, ProjectBoundaries.PathComparison)))
            {
                throw new BoundaryViolationException(
                    $"Project '{evaluation.ProjectName}' evaluates project reference '{referencePath}' outside its allowed reference boundary. "
                    + $"Offending edge: '{referencePath}'.");
            }
        }
    }

    public static void AssertNoPackages(ProjectEvaluation evaluation)
    {
        if (evaluation.PackageReferenceItems.Count > 0)
        {
            throw new BoundaryViolationException(
                $"Project '{evaluation.ProjectName}' must not depend on packages, but evaluates package references: "
                + string.Join(", ", evaluation.PackageIds.Select(id => $"'{id}'")) + ".");
        }
    }

    public static void AssertManagedNet10(ProjectEvaluation evaluation)
    {
        var targetFramework = evaluation.PropertyValue("TargetFramework");
        if (!string.Equals(targetFramework, "net10.0", StringComparison.OrdinalIgnoreCase))
        {
            throw new BoundaryViolationException(
                $"Project '{evaluation.ProjectName}' must target the managed net10.0 framework, but evaluates TargetFramework '{targetFramework}'.");
        }

        if (IsEnabled(evaluation.PropertyValue("PublishAot")))
        {
            throw new BoundaryViolationException(
                $"Project '{evaluation.ProjectName}' must stay on managed execution, but evaluates PublishAot 'true'.");
        }

        if (IsEnabled(evaluation.PropertyValue("PublishTrimmed")))
        {
            throw new BoundaryViolationException(
                $"Project '{evaluation.ProjectName}' must stay on managed execution, but evaluates PublishTrimmed 'true'.");
        }
    }

    public static void AssertCompileSourcesWithinRoot(ProjectEvaluation evaluation, string allowedRoot)
    {
        var canonicalRoot = ProjectBoundaries.Canonicalize(allowedRoot);
        foreach (var compilePath in evaluation.CompileItemPaths)
        {
            if (IsGeneratedOutput(evaluation.ProjectDirectory, compilePath))
            {
                continue;
            }

            if (!ProjectBoundaries.IsWithinRoot(compilePath, canonicalRoot))
            {
                throw new BoundaryViolationException(
                    $"Project '{evaluation.ProjectName}' evaluates compile input '{compilePath}' outside allowed root '{canonicalRoot}'. "
                    + $"Offending linked source: '{compilePath}'.");
            }
        }
    }

    private static bool IsGeneratedOutput(string projectDirectory, string compilePath) =>
        ProjectBoundaries.IsWithinRoot(compilePath, Path.Combine(projectDirectory, "obj"))
        || ProjectBoundaries.IsWithinRoot(compilePath, Path.Combine(projectDirectory, "bin"));

    private static bool IsEnabled(string propertyValue) =>
        bool.TryParse(propertyValue, out var enabled) && enabled;
}
