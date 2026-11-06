namespace Ansight.Host.Trends;

public sealed record WorkspaceTrendsSpanResolution(
    WorkspaceTrendsStatus Status,
    string Message,
    IReadOnlyList<WorkspaceTrendsSpanInstance> Instances);
