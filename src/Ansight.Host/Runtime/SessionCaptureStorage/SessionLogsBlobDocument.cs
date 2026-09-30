namespace Ansight.Host.Runtime.SessionCaptureStorage;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using Ansight.Host;

internal sealed class SessionLogsBlobDocument
{
    public const string SchemaName = "ansight.session-logs.v2";

    public string Schema { get; init; } = SchemaName;
    public required DateTimeOffset SavedAtUtc { get; init; }
    public required IReadOnlyList<LogEntry> Logs { get; init; }
}
