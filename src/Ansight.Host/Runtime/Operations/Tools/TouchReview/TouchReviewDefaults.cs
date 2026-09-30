namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal static class TouchReviewDefaults
{
    public const int DefaultTouchResultLimit = 500;
    public const int MaxTouchResultLimit = 5000;
    public const int DefaultTouchTimelineBucketCount = 24;
    public const int MaxTouchTimelineBucketCount = 200;
    public const int DefaultTouchContextRadius = 12;
    public const int MaxTouchContextRadius = 100;
    public const int DefaultGestureGapMilliseconds = 700;
    public const int MaxGestureGapMilliseconds = 10000;
    public const int DefaultTouchArtifactWindowSeconds = 10;
    public const int MaxTouchArtifactWindowSeconds = 3600;
    public const int DefaultTouchArtifactLimitPerType = 5;
    public const int MaxTouchArtifactLimitPerType = 50;
    public const int DefaultTapTargetWindowMilliseconds = 1500;
    public const int DefaultDeadTouchResponseWindowMilliseconds = 1200;
    public const int DefaultHeatmapColumns = 6;
    public const int DefaultHeatmapRows = 10;
    public const int MaxHeatmapDimension = 100;
    public const int LongPressMinimumMilliseconds = 600;
    public const double TapMaximumNormalizedDistance = 0.035d;
}
