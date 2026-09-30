namespace Ansight.Host.Runtime.Operations.Tools.LogReview;

internal sealed record LogReviewSession(
    AppSessionSnapshot Snapshot,
    bool IsLive,
    string PlatformKey,
    string OperatingSystemName);
