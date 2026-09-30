namespace Ansight.Host.Runtime.Operations.Tools.LogReview;

internal sealed record LogReviewSessionFacet(
    LogReviewSession Session,
    int LogCount,
    DateTimeOffset FirstLogUtc,
    DateTimeOffset LastLogUtc);
