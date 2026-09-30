namespace Ansight.Host.Replay;

public sealed record SessionLiveUpdateCursor(
    int LogIndex,
    int ImageIndex,
    int TouchIndex,
    int NetworkRequestIndex,
    int VisualTreeIndex,
    int ArtifactIndex,
    int MetricIndex,
    int MetricChannelCount,
    int AnnotationCount,
    int AnalysisCount)
{
    public SessionLiveUpdateCursor(
        int logIndex,
        int imageIndex,
        int touchIndex,
        int visualTreeIndex,
        int artifactIndex,
        int metricIndex,
        int metricChannelCount,
        int annotationCount,
        int analysisCount)
        : this(
            logIndex,
            imageIndex,
            touchIndex,
            0,
            visualTreeIndex,
            artifactIndex,
            metricIndex,
            metricChannelCount,
            annotationCount,
            analysisCount)
    {
    }
}
