namespace Ansight.Host.Workspaces.Authoring;

internal static class SessionSanitizerModuleContractArtifacts
{
    private static readonly Lazy<string> typeDefinitions = new(() =>
        EmbeddedTextResource.Read("Workspaces/Authoring/Resources/ansight-sanitizer.d.ts").TrimEnd());
    private static readonly Lazy<string> defaultModuleSource = new(() =>
        EmbeddedTextResource.Read("Workspaces/Authoring/Resources/default-sanitizer.ts").TrimEnd());

    public static string GetTypeDefinitions() => typeDefinitions.Value;

    public static string GetDefaultModuleSource() => defaultModuleSource.Value;
}
