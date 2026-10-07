using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SolusAgent.ContractTests.Architecture;

/// <summary>
/// Owned temporary synthetic Api-only consumer fixture. It references the actual Api project and
/// carries real source files so evaluation produces nonempty reference and compile inputs without
/// copying any downstream source. All generated MSBuild XML is serialized through XDocument and
/// XAttribute so legal path characters such as ampersands round-trip through real evaluation.
/// Only verified owned temporary paths are deleted.
/// </summary>
internal sealed class SyntheticConsumerWorkspace : IDisposable
{
    private const string WorkspacePrefix = "solusagent-issue12-";

    private const string ExtensionsPropsFileName = "Extensions.props";

    /// <summary>Legal Windows/POSIX path characters that require XML attribute escaping.</summary>
    public const string XmlSpecialPathToken = "xml & ' special";

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

    private const string SupportSource =
        """
        namespace Consumer;

        internal static class Support
        {
            public static string Describe() => "support";
        }
        """;

    public SyntheticConsumerWorkspace(string? pathToken = null)
    {
        var uniqueToken = Guid.NewGuid().ToString("N");
        var directoryName = pathToken is null
            ? WorkspacePrefix + uniqueToken
            : WorkspacePrefix + uniqueToken + "-" + pathToken;

        WorkspaceRoot = ProjectBoundaries.Canonicalize(Path.Combine(Path.GetTempPath(), directoryName));
        OutsideRoot = ProjectBoundaries.Canonicalize(WorkspaceRoot + "-outside");
        ConsumerDirectory = Path.Combine(WorkspaceRoot, "Consumer");

        Directory.CreateDirectory(ConsumerDirectory);
        Directory.CreateDirectory(Path.Combine(WorkspaceRoot, "Linked"));
        Directory.CreateDirectory(OutsideRoot);

        File.WriteAllText(Path.Combine(ConsumerDirectory, "BusinessLogic.cs"), BusinessLogicSource);
        File.WriteAllText(LinkedSharedPath, LinkedSharedSource);
        File.WriteAllText(OutsideSourcePath, OutsideSource);

        WriteExtensionsProps();
        WriteConsumerProject();
    }

    public string WorkspaceRoot { get; }

    public string OutsideRoot { get; }

    public string ConsumerDirectory { get; }

    public string ConsumerProjectPath => Path.Combine(ConsumerDirectory, "Consumer.csproj");

    public string ExtensionsPropsPath => Path.Combine(ConsumerDirectory, ExtensionsPropsFileName);

    public string BusinessLogicPath => Path.Combine(ConsumerDirectory, "BusinessLogic.cs");

    public string LinkedSharedPath => Path.Combine(WorkspaceRoot, "Linked", "Shared.cs");

    public string OutsideSourcePath => Path.Combine(OutsideRoot, "Outside.cs");

    public void AddDirectProjectReference(string projectPath) =>
        WriteConsumerProject(ProjectReferenceItemGroup(projectPath));

    public void AddImportedProjectReference(string projectPath) =>
        WriteExtensionsProps(ProjectReferenceItemGroup(projectPath));

    public void AddLinkedOutsideCompileItem() =>
        WriteConsumerProject(
            new XElement(
                "ItemGroup",
                new XElement(
                    "Compile",
                    new XAttribute("Include", $"../../{Path.GetFileName(OutsideRoot)}/Outside.cs"),
                    new XAttribute("Link", "Downstream/Outside.cs"))));

    public string WriteSupportProject(string projectName)
    {
        var supportDirectory = Path.Combine(WorkspaceRoot, "Support", projectName);
        Directory.CreateDirectory(supportDirectory);

        var supportProjectPath = Path.Combine(supportDirectory, projectName + ".csproj");
        SaveDocument(
            new XElement(
                "Project",
                new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement(
                    "PropertyGroup",
                    new XElement("TargetFramework", "net10.0"),
                    new XElement("ImplicitUsings", "enable"),
                    new XElement("Nullable", "enable"))),
            supportProjectPath);

        File.WriteAllText(Path.Combine(supportDirectory, "Support.cs"), SupportSource);
        return supportProjectPath;
    }

    public void Dispose()
    {
        DeleteOwnedDirectory(WorkspaceRoot);
        DeleteOwnedDirectory(OutsideRoot);
    }

    private static XElement ProjectReferenceItemGroup(string projectPath) =>
        new(
            "ItemGroup",
            new XElement("ProjectReference", new XAttribute("Include", projectPath)));

    private void WriteConsumerProject(params XElement[] additionalItemGroups)
    {
        var project = new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"));

        project.Add(
            new XElement(
                "PropertyGroup",
                new XElement("TargetFramework", "net10.0"),
                new XElement("ImplicitUsings", "enable"),
                new XElement("Nullable", "enable"),
                new XElement("TreatWarningsAsErrors", "true")));

        project.Add(ProjectReferenceItemGroup(RepositoryLayout.ProductionProjectPath("SolusAgent.Api")));

        project.Add(
            new XElement(
                "ItemGroup",
                new XElement(
                    "Compile",
                    new XAttribute("Include", "../Linked/Shared.cs"),
                    new XAttribute("Link", "Linked/Shared.cs"))));

        foreach (var itemGroup in additionalItemGroups)
        {
            project.Add(itemGroup);
        }

        project.Add(new XElement("Import", new XAttribute("Project", ExtensionsPropsFileName)));

        SaveDocument(project, ConsumerProjectPath);
    }

    private void WriteExtensionsProps(params XElement[] additionalItemGroups)
    {
        var project = new XElement("Project");
        foreach (var itemGroup in additionalItemGroups)
        {
            project.Add(itemGroup);
        }

        SaveDocument(project, ExtensionsPropsPath);
    }

    private static void SaveDocument(XElement projectElement, string path)
    {
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), projectElement);
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
        };

        using var stream = File.Create(path);
        using var writer = XmlWriter.Create(stream, settings);
        document.Save(writer);
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
