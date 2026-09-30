namespace Ansight.Host.Runtime.SessionCaptureStorage;

internal sealed class SessionTouchesBlobDocument
{
    public const string SchemaName = SessionTouchPacking.SchemaName;

    public string Schema { get; init; } = SchemaName;
    public required DateTimeOffset SavedAtUtc { get; init; }
    public List<SessionTouchPackedBatch> Batches { get; init; } = [];
}
