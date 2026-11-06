using Ansight.Host.AppGraphs;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal static class NavigationGuidance
{
    public static string Read(string framework)
    {
        var guidance = EmbeddedTextResource.ReadSection(
            "SimulatorAgent/Prompts/navigation.md",
            framework);
        var paradigms = AppGraphNavigationTechnologyCatalog.GetKinds(framework).Select(kind =>
            $"{kind.Kind} ({kind.Paradigm.Topology}): {kind.Paradigm.ChildInventory} "
                + $"Selection: {kind.Paradigm.Selection} Return: {kind.Paradigm.ReturnBehavior} "
                + $"Evidence: {kind.Paradigm.Evidence}");

        return guidance + "\nTechnology-specific navigation paradigms:\n" + string.Join("\n", paradigms);
    }
}
