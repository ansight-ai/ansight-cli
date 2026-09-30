namespace Ansight.Host.AppGraphs;

public sealed record LocalAppGraphNavigationDiscoveryResult(
    bool IsSuccess,
    string Message,
    IReadOnlyList<LocalAppGraphNavigationController> Controllers);
