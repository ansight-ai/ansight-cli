namespace Ansight.Host.Workspaces.Authoring;

internal static class WorkspaceDefinitionSchemaArtifacts
{
    private static readonly Lazy<string> testDefinitionV1 = new(() =>
        Read("test-definition.v1.schema.json"));
    private static readonly Lazy<string> trendsDefinitionV1 = new(() =>
        Read("trends-definition.v1.schema.json"));

    public static string TestDefinitionV1 => testDefinitionV1.Value;

    public static string TrendsDefinitionV1 => trendsDefinitionV1.Value;

    private static string Read(string fileName)
        => EmbeddedTextResource.Read($"Workspaces/Authoring/Resources/{fileName}").TrimEnd();
}
