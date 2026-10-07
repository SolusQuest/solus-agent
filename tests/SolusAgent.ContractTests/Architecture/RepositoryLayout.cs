namespace SolusAgent.ContractTests.Architecture;

/// <summary>Locates the repository root from the built test's location using the solution and expected project markers.</summary>
internal static class RepositoryLayout
{
    private static readonly string[] ProjectMarkerPaths =
    [
        Path.Combine("src", "SolusAgent.Api", "SolusAgent.Api.csproj"),
        Path.Combine("src", "SolusAgent.Tools.Api", "SolusAgent.Tools.Api.csproj"),
        Path.Combine("src", "SolusAgent.Runtime.Api", "SolusAgent.Runtime.Api.csproj"),
        Path.Combine("src", "SolusAgent.Runtime", "SolusAgent.Runtime.csproj"),
        Path.Combine("tests", "SolusAgent.ContractTests", "SolusAgent.ContractTests.csproj"),
    ];

    private static readonly Lazy<string> RepositoryRoot = new(LocateRepositoryRoot);

    public static string Root => RepositoryRoot.Value;

    public static string SolutionPath => Path.Combine(Root, "SolusAgent.slnx");

    public static string TestProjectPath =>
        Path.Combine(Root, "tests", "SolusAgent.ContractTests", "SolusAgent.ContractTests.csproj");

    public static string ProductionProjectPath(string projectName) =>
        Path.Combine(Root, "src", projectName, projectName + ".csproj");

    public static string ProjectName(string projectPath) => Path.GetFileNameWithoutExtension(projectPath);

    private static string LocateRepositoryRoot()
    {
        for (string? directory = Path.GetFullPath(AppContext.BaseDirectory); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (File.Exists(Path.Combine(directory, "SolusAgent.slnx"))
                && ProjectMarkerPaths.All(marker => File.Exists(Path.Combine(directory, marker))))
            {
                return directory;
            }
        }

        throw new InvalidOperationException(
            $"Unable to locate the repository root from test base directory '{AppContext.BaseDirectory}'. "
            + "Expected a directory containing SolusAgent.slnx and the four production project files.");
    }
}
