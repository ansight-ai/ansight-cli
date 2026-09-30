namespace Ansight.Host.Runtime.Operations.Tools.LogReview;

internal sealed record LogReviewFilters(
    DateTimeOffset? StartUtc,
    DateTimeOffset? EndUtc,
    LogPriority? MinimumVerbosity,
    IReadOnlyList<string> StreamIds,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Sources,
    string? Query);
