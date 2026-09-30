namespace Ansight.Host.Runtime.State;

using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Pairing.Models;

internal interface ISessionIngestion
{
    void AddSessionLog(string sessionId, string message);
    void AddSessionLogs(string sessionId, IReadOnlyList<string> messages);
    void AddSessionLog(string sessionId, LogEntry entry);
    void AddSessionLogs(string sessionId, IReadOnlyList<LogEntry> entries);
    void EnsureSessionLogStream(string sessionId, SessionLogStream stream);

    void SetSessionLogStreamStatus(
        string sessionId,
        string streamId,
        string status,
        string? statusMessage = null,
        DateTimeOffset? endedUtc = null);

    void AddSessionLogEntries(string sessionId, string streamId, IReadOnlyList<LogEntry> entries);
    void UpdateSessionMetricChannels(string sessionId, IReadOnlyList<SessionMetricChannel> channels);
    void AddSessionMetrics(string sessionId, IReadOnlyList<SessionMetricSample> metrics, int telemetrySegmentId);
    void AddSessionApplicationEvents(string sessionId, IReadOnlyList<SessionApplicationEvent> events);
    void AddSessionTouches(string sessionId, IReadOnlyList<SessionTouchInputRecord> touches);
    void AddSessionNetworkRequests(string sessionId, IReadOnlyList<SessionNetworkRequest> requests);
    void SetSessionDeviceProfile(string sessionId, DeviceAppProfile? profile, string? profileJson);
    void SetSessionCustomProperties(string sessionId, JsonObject? customProperties);
    void SetSessionAppToolCatalog(string sessionId, SessionAppToolCatalogSnapshot catalog);

    void AddSessionImage(
        string sessionId,
        DateTimeOffset capturedAtUtc,
        string format,
        int width,
        int height,
        int quality,
        ReadOnlyMemory<byte> bytes);

    Task<SessionImageFrame?> AddSessionEvidenceImageAsync(
        string sessionId,
        DateTimeOffset capturedAtUtc,
        string format,
        int width,
        int height,
        int quality,
        ReadOnlyMemory<byte> bytes);

    void AddSessionAnalysis(string sessionId, SessionAnalysisRecord analysis);
    OperationResult AddSessionVisualTreeSnapshot(string sessionId, SessionVisualTreeSnapshot snapshot);
    SessionVisualTreeIngestionResult ReceiveSessionVisualTreeSnapshot(
        string sessionId,
        SessionVisualTreeSnapshot snapshot);

    OperationResult AddSessionArtifactSnapshot(
        string sessionId,
        SessionArtifactSnapshot snapshot,
        string sourceDirectoryPath);
}
