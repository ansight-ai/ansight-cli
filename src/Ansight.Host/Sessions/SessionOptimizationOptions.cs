namespace Ansight.Host.Sessions;

public sealed record SessionVisualTreeTypeSelection(
    string VisualTreeKind,
    string VisualTreeFormat,
    string RuntimePlatform,
    string Source)
{
    public bool Matches(SessionVisualTreeSnapshot snapshot)
        => string.Equals(VisualTreeKind, snapshot.VisualTreeKind, StringComparison.Ordinal)
           && string.Equals(VisualTreeFormat, snapshot.VisualTreeFormat, StringComparison.Ordinal)
           && string.Equals(RuntimePlatform, snapshot.RuntimePlatform, StringComparison.Ordinal)
           && (string.IsNullOrWhiteSpace(Source)
               || string.Equals(Source, snapshot.Source, StringComparison.Ordinal));
}

public sealed record SessionOptimizationOptions(
    bool OptimizeScreenshots = true,
    IReadOnlyList<SessionVisualTreeTypeSelection>? VisualTreeTypes = null)
{
    public bool ShouldOptimizeVisualTree(SessionVisualTreeSnapshot snapshot)
        => VisualTreeTypes is null || VisualTreeTypes.Any(selection => selection.Matches(snapshot));
}
