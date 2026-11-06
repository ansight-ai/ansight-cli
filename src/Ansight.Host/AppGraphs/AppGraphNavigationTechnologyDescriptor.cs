using System.Text.Json.Nodes;
using Ansight.Host.SimulatorAgent;

namespace Ansight.Host.AppGraphs;

public sealed record AppGraphNavigationTechnologyDescriptor(
    string Framework,
    string Kind,
    string NavigationToolId,
    string StructureFingerprint)
{
    public AppGraphNavigationParadigm? Paradigm => AppGraphNavigationTechnologyCatalog
        .GetKinds(Framework).FirstOrDefault(kind => string.Equals(
            kind.Kind, Kind?.Trim(), StringComparison.OrdinalIgnoreCase))?.Paradigm;
}
