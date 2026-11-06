namespace Ansight.Host.Sessions;

public sealed record SessionOptimizationOptions(
    bool OptimizeScreenshots = true,
    IReadOnlyList<SessionVisualTreeTypeSelection>? VisualTreeTypes = null)
{
    public bool ShouldOptimizeVisualTree(SessionVisualTreeSnapshot snapshot)
        => VisualTreeTypes is null || VisualTreeTypes.Any(selection => selection.Matches(snapshot));
}
