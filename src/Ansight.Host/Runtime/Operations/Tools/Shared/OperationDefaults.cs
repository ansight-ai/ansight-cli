namespace Ansight.Host.Runtime.Operations.Tools.Shared;

internal static class OperationDefaults
{
    /// <summary>
    /// Default Segoe MDL2 glyph used when host operations create a known app entry without an explicit icon.
    /// </summary>
    public const string DefaultKnownAppIconGlyph = "\ue7ba";

    /// <summary>
    /// Maximum visual-tree depth applied when proxying app tool calls that omit an explicit depth.
    /// </summary>
    public const int DefaultVisualTreeMaxDepth = 32;

    /// <summary>
    /// Maximum screenshot width applied when proxying screenshot-producing app tool calls that omit a width.
    /// </summary>
    public const int DefaultScreenshotMaxWidth = 1440;

    /// <summary>
    /// JPEG quality applied when proxying screenshot-producing app tool calls that omit a quality value.
    /// </summary>
    public const int DefaultScreenshotQuality = 80;

    /// <summary>
    /// Default number of log entries returned by log retrieval and export tools.
    /// </summary>
    public const int DefaultLogResultLimit = 200;

    /// <summary>
    /// Maximum number of log entries a single host operation may return or export.
    /// </summary>
    public const int MaxLogResultLimit = 5000;

    /// <summary>
    /// Default number of neighboring log entries returned before and after a target log.
    /// </summary>
    public const int DefaultLogContextRadius = 20;

    /// <summary>
    /// Maximum number of neighboring log entries allowed on either side of a target log.
    /// </summary>
    public const int MaxLogContextRadius = 200;

    /// <summary>
    /// Default number of top log facet values, repeated messages, or tags returned in summaries.
    /// </summary>
    public const int DefaultLogReviewTopCount = 10;

    /// <summary>
    /// Maximum number of top log facet values, repeated messages, or tags returned in summaries.
    /// </summary>
    public const int MaxLogReviewTopCount = 50;

    /// <summary>
    /// Default number of buckets used when building a log timeline.
    /// </summary>
    public const int DefaultLogTimelineBucketCount = 24;

    /// <summary>
    /// Maximum number of buckets allowed in a log timeline response.
    /// </summary>
    public const int MaxLogTimelineBucketCount = 200;

    /// <summary>
    /// Default number of telemetry samples returned by the telemetry inspection tool.
    /// </summary>
    public const int DefaultTelemetryResultLimit = 2000;

    /// <summary>
    /// Maximum number of telemetry samples returned by the telemetry inspection tool.
    /// </summary>
    public const int MaxTelemetryResultLimit = 10000;

    /// <summary>
    /// Default number of annotations returned by annotation inspection tools.
    /// </summary>
    public const int DefaultAnnotationResultLimit = 200;

    /// <summary>
    /// Maximum number of annotations returned by annotation inspection tools.
    /// </summary>
    public const int MaxAnnotationResultLimit = 2000;

    /// <summary>
    /// Source value stored on annotations created through host operations when the caller does not provide one.
    /// </summary>
    public const string DefaultInjectedAnnotationSource = "ansight-operation";

    /// <summary>
    /// Default number of seconds on either side of a target timestamp used by nearest-artifact lookup.
    /// </summary>
    public const int DefaultNearestArtifactWindowSeconds = 30;

    /// <summary>
    /// Maximum number of seconds on either side of a target timestamp allowed by nearest-artifact lookup.
    /// </summary>
    public const int MaxNearestArtifactWindowSeconds = 3600;

    /// <summary>
    /// Default number of nearest entries returned per artifact category.
    /// </summary>
    public const int DefaultNearestArtifactLimitPerType = 3;

    /// <summary>
    /// Maximum number of nearest entries returned per artifact category.
    /// </summary>
    public const int MaxNearestArtifactLimitPerType = 20;

    /// <summary>
    /// Default number of exception groups returned by exception extraction.
    /// </summary>
    public const int DefaultExceptionGroupLimit = 50;

    /// <summary>
    /// Maximum number of exception groups returned by exception extraction.
    /// </summary>
    public const int MaxExceptionGroupLimit = 200;

    /// <summary>
    /// Default number of entries returned for each session-artifact manifest category.
    /// </summary>
    public const int DefaultArtifactManifestLimit = 200;

    /// <summary>
    /// Maximum number of entries returned for each session-artifact manifest category.
    /// </summary>
    public const int MaxArtifactManifestLimit = 5000;
}
