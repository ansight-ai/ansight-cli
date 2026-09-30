namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal static class NavigationGuidance
{
    public static string Read(string framework)
        => EmbeddedTextResource.ReadSection(
            "SimulatorAgent/Prompts/navigation.md",
            framework);
}
