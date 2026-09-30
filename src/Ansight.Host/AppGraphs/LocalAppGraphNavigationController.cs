namespace Ansight.Host.AppGraphs;

public sealed record LocalAppGraphNavigationController(
    string Framework,
    string Label,
    string NavigationToolId,
    string StructureFingerprint,
    string Guidance,
    IReadOnlyList<AppGraphNavigationTechnologyKind> Kinds);
