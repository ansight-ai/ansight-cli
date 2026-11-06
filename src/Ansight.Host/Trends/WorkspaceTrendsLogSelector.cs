namespace Ansight.Host.Trends;

public sealed record WorkspaceTrendsLogSelector(
    string StreamId,
    string Message,
    string Match = "exact",
    string? Priority = null,
    string? Source = null,
    string? Tag = null,
    int? ProcessId = null);
