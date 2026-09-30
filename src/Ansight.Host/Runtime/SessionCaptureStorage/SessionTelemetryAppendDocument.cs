namespace Ansight.Host.Runtime.SessionCaptureStorage;

using Ansight.Host;

internal sealed class SessionTelemetryAppendDocument
{
    public const string SchemaName = "ansight.session-telemetry-append.v1";

    public string Schema { get; init; } = SchemaName;
    public required DateTimeOffset SavedAtUtc { get; init; }
    public bool ReplaceAll { get; init; }
    public required IReadOnlyList<SessionMetricSample> Metrics { get; init; }
}
