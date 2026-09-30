namespace Ansight.Host.Runtime.SessionCaptureStorage;

internal sealed class SessionApplicationEventsBlobDocument
{
    public string Schema { get; init; } = "ansight.session-application-events.v1";
    public required DateTimeOffset SavedAtUtc { get; init; }
    public required IReadOnlyList<SessionApplicationEvent> Events { get; init; }
}
