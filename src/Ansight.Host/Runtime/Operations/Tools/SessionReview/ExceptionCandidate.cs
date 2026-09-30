namespace Ansight.Host.Runtime.Operations.Tools.SessionReview;

internal sealed record ExceptionCandidate(
    SessionLogReviewEntry Entry,
    string ExceptionType,
    string Summary,
    string GroupKey,
    int StackFrameCount);
