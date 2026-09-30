using System.Reflection;

namespace Ansight.Host.Workspaces.Authoring;

internal static class WorkspaceReadmeTemplateArtifacts
{
    private static readonly Assembly assembly = typeof(WorkspaceReadmeTemplateArtifacts).Assembly;

    public static IReadOnlyList<WorkspaceReadmeTemplate> All { get; } =
    [
        Create(string.Empty, "ansight"),
        Create("tasks", "tasks"),
        Create("tests", "tests"),
        Create("triggers", "triggers"),
        Create("trends", "trends"),
        Create("sanitizers", "sanitizers"),
        Create("schema", "schema")
    ];

    private static WorkspaceReadmeTemplate Create(string relativeDirectoryPath, string assetName)
        => new(relativeDirectoryPath, Read(assetName));

    private static string Read(string assetName)
    {
        var resourceSuffix = $".ReadmeTemplates.{assetName}.md";
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(resourceSuffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Embedded workspace README template '{assetName}' was not found.");
        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException(
                               $"Embedded workspace README template '{resourceName}' could not be opened.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

internal sealed record WorkspaceReadmeTemplate(
    string RelativeDirectoryPath,
    string Content);
