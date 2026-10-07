namespace SolusAgent.ContractTests.Architecture;

/// <summary>Canonical path normalization and segment-aware containment shared by the boundary checks.</summary>
internal static class ProjectBoundaries
{
    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string Canonicalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static string CanonicalizeFromDirectory(string baseDirectory, string path) =>
        Canonicalize(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));

    public static bool IsWithinRoot(string candidatePath, string allowedRoot)
    {
        var candidate = Canonicalize(candidatePath);
        var root = Canonicalize(allowedRoot);

        if (string.Equals(candidate, root, PathComparison))
        {
            return true;
        }

        return candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }
}
