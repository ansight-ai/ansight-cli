namespace Ansight.Host.Runtime.SessionCaptureStorage;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using Ansight.Host;

internal sealed class SessionTelemetryBlobDocument
{
    public const string SchemaName = "ansight.session-telemetry.v3";

    public string Schema { get; init; } = SchemaName;
    public required DateTimeOffset SavedAtUtc { get; init; }
    public required byte ChannelId { get; init; }
    public string? ChannelName { get; init; }
    public required string TelemetryType { get; init; }
    public required IReadOnlyList<SessionMetricSample> Metrics { get; init; }
}
