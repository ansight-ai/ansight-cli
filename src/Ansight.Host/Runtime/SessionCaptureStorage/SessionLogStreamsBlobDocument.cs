namespace Ansight.Host.Runtime.SessionCaptureStorage;

using Ansight.Host;

internal sealed class SessionLogStreamsBlobDocument
{
    public const string SchemaName = "ansight.session-log-streams.v1";

    public string Schema { get; init; } = SchemaName;
    public required DateTimeOffset SavedAtUtc { get; init; }
    public required IReadOnlyList<SessionLogStream> Streams { get; init; }
}
