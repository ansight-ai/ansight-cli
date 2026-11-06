using System.Text.Json.Nodes;
using Ansight.Host.AppGraphs;

namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentAppGraphLiveTabGroup(
    string Id,
    string ParentDestinationId,
    string SelectedDestinationId,
    IReadOnlyList<string> TabDestinationIds,
    decimal Confidence,
    DateTimeOffset FirstObservedUtc,
    DateTimeOffset UpdatedUtc,
    AppGraphNavigationTechnologyDescriptor? Technology = null);
