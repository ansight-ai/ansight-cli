namespace Ansight.Host.Models.Session;



public enum SessionLoadStage
{
    Header,
    DeviceProfile,
    Analyses,
    Annotations,
    Images,
    Touches,
    NetworkRequests,
    VisualTreeSnapshots,
    ArtifactSnapshots,
    Logs,
    MetricChannels,
    Telemetry,
    Complete
}
