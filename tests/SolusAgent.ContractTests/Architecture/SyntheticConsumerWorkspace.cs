namespace SolusAgent.ContractTests.Architecture;

/// <summary>
/// Owned temporary synthetic Api-only consumer fixture. It references the actual Api project and
/// carries real source files so evaluation produces nonempty reference and compile inputs without
/// copying any downstream source. Only verified owned temporary paths are deleted.
/// </summary>
internal sealed class SyntheticConsumerWorkspace : IDisposable
{
    private const string WorkspacePrefix = "solusagent-issue12-";

    private const string BusinessLogicSource =
        """
        namespace Consumer;

        public sealed class BusinessLogic
        {
            public string Name => "business";
        }
        """;

    private const string LinkedSharedSource =
        """
        namespace Consumer;

        internal static class LinkedShared
        {
            public static string Describe() => "shared";
        }
        """;

    private const string OutsideSource =
        """
        namespace Consumer;

        internal static class Outside
        {
            public static string Describe() => "outside";
        }
        """;

    public SyntheticConsumerWorkspace()
    {
        var token = Guid.NewGuid().ToString("N");
        WorkspaceRoot = ProjectBoundaries.Canonicalize(Path.Combine(Path.GetTempPath(), WorkspacePrefix + token));
        OutsideRoot = ProjectBoundaries.Canonicalize(WorkspaceRoot + "-outside");
        ConsumerDirectory = Path.Combine(WorkspaceRoot, "Consumer");

        Directory.CreateDirectory(ConsumerDirectory);
        Directory.CreateDirectory(Path.Combine(WorkspaceRoot, "Linked"));
        Directory.CreateDirectory(OutsideRoot);

        File.WriteAllText(Path.Combine(ConsumerDirectory, "BusinessLogic.cs"), BusinessLogicSource);
        File.WriteAllText(LinkedSharedPath, LinkedSharedSource);
        File.WriteAllText(OutsideSourcePath, OutsideSource);

        WriteExtensionsProps(string.Empty);
        WriteConsumerProject(string.Empty);
    }

    public string WorkspaceRoot { get; }

    public string OutsideRoot { get; }

    public string ConsumerDirectory { get; }

    public string ConsumerProjectPath => Path.Combine(ConsumerDirectory, "Consumer.csproj");

    public string ExtensionsPropsPath => Path.Combine(ConsumerDirectory, "Extensions.props");

    public string BusinessLogicPath => Path.Combine(ConsumerDirectory, "BusinessLogic.cs");

    public string LinkedSharedPath => Path.Combine(WorkspaceRoot, "Linked", "Shared.cs");

    public string OutsideSourcePath => Path.Combine(OutsideRoot, "Outside.cs");

    public void AddDirectProjectReference(string projectPath) =>
        WriteConsumerProject($"  <ItemGroup>\n    <ProjectReference Include=\"{projectPath}\" />\n  </ItemGroup>\n");

    public void AddImportedProjectReference(string projectPath) =>
        WriteExtensionsProps($"  <ItemGroup>\n    <ProjectReference Include=\"{projectPath}\" />\n  </ItemGroup>\n");

    public void AddLinkedOutsideCompileItem() =>
        WriteConsumerProject(
            "  <ItemGroup>\n"
            + $"    <Compile Include=\"../../{Path.GetFileName(OutsideRoot)}/Outside.cs\" Link=\"Downstream/Outside.cs\" />\n"
            + "  </ItemGroup>\n");

    public void Dispose()
    {
        DeleteOwnedDirectory(WorkspaceRoot);
        DeleteOwnedDirectory(OutsideRoot);
    }

    private void WriteConsumerProject(string extraItemGroups)
    {
        var content =
            $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
              </PropertyGroup>

              <ItemGroup>
                <ProjectReference Include="{RepositoryLayout.ProductionProjectPath("SolusAgent.Api")}" />
              </ItemGroup>

              <ItemGroup>
                <Compile Include="../Linked/Shared.cs" Link="Linked/Shared.cs" />
              </ItemGroup>

            {extraItemGroups}  <Import Project="Extensions.props" />

            </Project>
            """;

        File.WriteAllText(ConsumerProjectPath, content + "\n");
    }

    private void WriteExtensionsProps(string extraItemGroups)
    {
        var content =
            $"""
            <Project>

            {extraItemGroups}</Project>
            """;

        File.WriteAllText(ExtensionsPropsPath, content + "\n");
    }

    private static void DeleteOwnedDirectory(string canonicalPath)
    {
        var tempRoot = ProjectBoundaries.Canonicalize(Path.GetTempPath());
        if (!ProjectBoundaries.IsWithinRoot(canonicalPath, tempRoot)
            || !Path.GetFileName(canonicalPath).StartsWith(WorkspacePrefix, StringComparison.Ordinal))
        {
            return;
        }

        if (Directory.Exists(canonicalPath))
        {
            Directory.Delete(canonicalPath, recursive: true);
        }
    }
}
