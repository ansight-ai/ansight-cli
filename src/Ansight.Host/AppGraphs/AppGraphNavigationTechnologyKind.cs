using System.Text.Json.Nodes;
using Ansight.Host.SimulatorAgent;

namespace Ansight.Host.AppGraphs;

public sealed record AppGraphNavigationTechnologyKind(
    string Kind,
    string Label,
    string NormalizedRole,
    bool SupportsNavigationHost,
    bool SupportsTabGroup)
{
    public AppGraphNavigationParadigm Paradigm { get; init; } =
        AppGraphNavigationParadigms.Resolve("native-unknown", "generic_host");
}
