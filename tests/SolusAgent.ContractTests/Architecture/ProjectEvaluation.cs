using System.Text.Json;

namespace SolusAgent.ContractTests.Architecture;

/// <summary>The evaluated items and properties of one real project file, including imported and conditioned items.</summary>
internal sealed class ProjectEvaluation
{
    private readonly IReadOnlyList<IReadOnlyDictionary<string, string>> _projectReferenceItems;
    private readonly IReadOnlyList<IReadOnlyDictionary<string, string>> _packageReferenceItems;
    private readonly IReadOnlyList<IReadOnlyDictionary<string, string>> _compileItems;

    private ProjectEvaluation(
        string projectPath,
        IReadOnlyDictionary<string, string> properties,
        IReadOnlyList<IReadOnlyDictionary<string, string>> projectReferenceItems,
        IReadOnlyList<IReadOnlyDictionary<string, string>> packageReferenceItems,
        IReadOnlyList<IReadOnlyDictionary<string, string>> compileItems)
    {
        ProjectPath = projectPath;
        ProjectName = RepositoryLayout.ProjectName(projectPath);
        ProjectDirectory = Path.GetDirectoryName(projectPath)
            ?? throw new InvalidOperationException($"Project path has no directory: '{projectPath}'.");
        Properties = properties;
        _projectReferenceItems = projectReferenceItems;
        _packageReferenceItems = packageReferenceItems;
        _compileItems = compileItems;
    }

    public string ProjectPath { get; }

    public string ProjectName { get; }

    public string ProjectDirectory { get; }

    public IReadOnlyDictionary<string, string> Properties { get; }

    public IReadOnlyList<IReadOnlyDictionary<string, string>> ProjectReferenceItems => _projectReferenceItems;

    public IReadOnlyList<IReadOnlyDictionary<string, string>> PackageReferenceItems => _packageReferenceItems;

    public IReadOnlyList<IReadOnlyDictionary<string, string>> CompileItems => _compileItems;

    public IReadOnlyList<string> ProjectReferencePaths =>
        _projectReferenceItems.Select(ItemPath).ToArray();

    public IReadOnlyList<string> CompileItemPaths =>
        _compileItems.Select(ItemPath).ToArray();

    public IReadOnlyList<string> PackageIds =>
        _packageReferenceItems.Select(item => Metadata(item, "Identity")).ToArray();

    public string PropertyValue(string propertyName) =>
        Properties.TryGetValue(propertyName, out var value) ? value : string.Empty;

    public static ProjectEvaluation FromJson(string projectPath, string evaluationOutput)
    {
        using var document = ParseEvaluationJson(projectPath, evaluationOutput);
        var root = document.RootElement;

        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("Properties", out var propertiesElement))
        {
            foreach (var property in propertiesElement.EnumerateObject())
            {
                properties[property.Name] = ScalarValue(property.Value);
            }
        }

        return new ProjectEvaluation(
            projectPath,
            properties,
            ReadItems(root, "ProjectReference"),
            ReadItems(root, "PackageReference"),
            ReadItems(root, "Compile"));
    }

    private static JsonDocument ParseEvaluationJson(string projectPath, string evaluationOutput)
    {
        var start = evaluationOutput.IndexOf('{');
        var end = evaluationOutput.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            throw new InvalidOperationException(
                $"MSBuild evaluation produced no JSON output for '{projectPath}'. Output: '{evaluationOutput}'");
        }

        try
        {
            return JsonDocument.Parse(evaluationOutput.Substring(start, end - start + 1));
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"MSBuild evaluation produced unparsable JSON for '{projectPath}'. Output: '{evaluationOutput}'",
                exception);
        }
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> ReadItems(JsonElement root, string itemType)
    {
        var items = new List<IReadOnlyDictionary<string, string>>();
        if (!root.TryGetProperty("Items", out var itemsElement)
            || !itemsElement.TryGetProperty(itemType, out var itemListElement))
        {
            return items;
        }

        foreach (var item in itemListElement.EnumerateArray())
        {
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in item.EnumerateObject())
            {
                metadata[entry.Name] = ScalarValue(entry.Value);
            }

            items.Add(metadata);
        }

        return items;
    }

    private static string ScalarValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Object when value.TryGetProperty("Value", out var nested) => nested.GetString() ?? string.Empty,
            _ => value.ToString(),
        };

    private static string Metadata(IReadOnlyDictionary<string, string> item, string name) =>
        item.TryGetValue(name, out var value) ? value : string.Empty;

    private string ItemPath(IReadOnlyDictionary<string, string> item)
    {
        var fullPath = Metadata(item, "FullPath");
        if (fullPath.Length > 0)
        {
            return ProjectBoundaries.Canonicalize(fullPath);
        }

        return ProjectBoundaries.CanonicalizeFromDirectory(ProjectDirectory, Metadata(item, "Identity"));
    }
}
