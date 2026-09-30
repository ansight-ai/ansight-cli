namespace Ansight.Host.Runtime.Operations.Tools.LogReview;

internal sealed record LogReviewSessionFilters(
    string? SessionId,
    string? AppId,
    IReadOnlyList<string> Platforms,
    bool IncludeHistorical,
    bool LiveOnly);
