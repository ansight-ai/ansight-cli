using System.Text.Json.Nodes;
using Ansight.Host.AppGraphs;

namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentAppGraphLiveNavigationHost(
    string Id,
    string Kind,
    string Name,
    string DestinationId,
    string ActiveChildDestinationId,
    IReadOnlyList<string> ChildDestinationIds,
    string Framework,
    decimal Confidence,
    DateTimeOffset FirstObservedUtc,
    DateTimeOffset UpdatedUtc,
    AppGraphNavigationTechnologyDescriptor? Technology = null);
