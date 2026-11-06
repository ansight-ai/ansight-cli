using System.Text.Json.Nodes;
using Ansight.Host.SimulatorAgent;

namespace Ansight.Host.AppGraphs;

public sealed record AppGraphNavigationFramework(
    string Framework,
    string Label,
    IReadOnlyList<AppGraphNavigationTechnologyKind> Kinds);
