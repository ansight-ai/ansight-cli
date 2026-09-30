namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal sealed class VisualTreeSearchCriteria
{
    public string? Query { get; init; }

    public string? SnapshotId { get; init; }

    public string? NodeId { get; init; }

    public string? Label { get; init; }

    public string? AutomationId { get; init; }

    public string? Type { get; init; }

    public double? NormalizedX { get; init; }

    public double? NormalizedY { get; init; }

    public int Limit { get; init; } = SessionEvidenceDefaults.DefaultVisualTreeSearchLimit;

    public bool HasPoint => NormalizedX.HasValue && NormalizedY.HasValue;

    public bool HasTextFilter
        => !string.IsNullOrWhiteSpace(Query)
           || !string.IsNullOrWhiteSpace(NodeId)
           || !string.IsNullOrWhiteSpace(Label)
           || !string.IsNullOrWhiteSpace(AutomationId)
           || !string.IsNullOrWhiteSpace(Type);
}
