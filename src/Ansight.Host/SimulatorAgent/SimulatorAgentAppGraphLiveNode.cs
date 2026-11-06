using System.Text.Json.Nodes;
using Ansight.Host.AppGraphs;

namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentAppGraphLiveNode(
    string Id,
    string Kind,
    string Name,
    string? ParentScreen,
    IReadOnlyList<string> Synonyms,
    string Purpose,
    string Description,
    string ScrollStatus,
    decimal Confidence,
    DateTimeOffset FirstObservedUtc,
    DateTimeOffset UpdatedUtc);
