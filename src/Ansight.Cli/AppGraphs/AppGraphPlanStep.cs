using Ansight.Host.Cloud;

namespace Ansight.Cli.AppGraphs;

internal sealed record AppGraphPlanStep(
    AppGraphDocumentEdge Edge,
    AppGraphDocumentNode From,
    AppGraphDocumentNode To,
    IReadOnlyList<CloudAppGraphBinding> Bindings);
