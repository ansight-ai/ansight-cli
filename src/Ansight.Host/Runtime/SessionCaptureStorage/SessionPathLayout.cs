namespace Ansight.Host.Runtime.SessionCaptureStorage;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using Ansight.Host;

internal sealed record SessionPathLayout(
    string AppDirectoryPath,
    string SessionDirectoryPath,
    string SummaryFilePath,
    string LogsFilePath,
    string LogStreamsFilePath,
    string LogSegmentsDirectoryPath,
    string DeviceProfileFilePath,
    string AppToolCatalogFilePath,
    string AnalysesFilePath,
    string AnnotationsFilePath,
    string AgentTaskLinksFilePath,
    string ImagesFilePath,
    string TouchesFilePath,
    string ApplicationEventsFilePath,
    string NetworkRequestsDirectoryPath,
    string VisualTreesDirectoryPath,
    string ArtifactsDirectoryPath,
    string MetricChannelsFilePath,
    string TelemetryDirectoryPath,
    string CapturedImagesDirectoryPath);
