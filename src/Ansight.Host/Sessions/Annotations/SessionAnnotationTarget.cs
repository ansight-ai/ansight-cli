namespace Ansight.Host.Models.Session;

public sealed class SessionAnnotationTarget
{
    public string Kind { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string TargetId { get; init; } = string.Empty;

    public string VisualTreeSnapshotId { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string ElementKind { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string AutomationId { get; init; } = string.Empty;

    public int Depth { get; init; }

    public int ChildCount { get; init; }

    public SessionAnnotationTargetBounds? AbsoluteBounds { get; init; }

    public SessionAnnotationTargetBounds? NormalizedBounds { get; init; }
}
