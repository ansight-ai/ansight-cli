namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal sealed record TouchGestureSegment(
    string GestureId,
    string Kind,
    IReadOnlyList<long> PointerIds,
    IReadOnlyList<SessionTouchInputRecord> Touches,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    long DurationMilliseconds,
    double? DistanceNormalized,
    double? MaxDistanceNormalized,
    bool IsComplete);
