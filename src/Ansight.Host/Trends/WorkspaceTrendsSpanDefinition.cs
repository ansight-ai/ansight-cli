namespace Ansight.Host.Trends;

public sealed record WorkspaceTrendsSpanDefinition(
    WorkspaceEventAnchor Start,
    WorkspaceEventAnchor End,
    WorkspaceTrendsSpanSelection Selection,
    TimeSpan MaximumDuration);
