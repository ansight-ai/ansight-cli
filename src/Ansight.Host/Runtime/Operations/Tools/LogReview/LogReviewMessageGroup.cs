namespace Ansight.Host.Runtime.Operations.Tools.LogReview;

internal sealed record LogReviewMessageGroup(
    string NormalizedMessage,
    string SampleMessage,
    int Count,
    DateTimeOffset FirstUtc,
    DateTimeOffset LastUtc);
