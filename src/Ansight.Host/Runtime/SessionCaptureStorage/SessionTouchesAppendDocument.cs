namespace Ansight.Host.Runtime.SessionCaptureStorage;

internal sealed class SessionTouchesAppendDocument
{
    public const string SchemaName = "ansight.touches-append.v1";

    public string Schema { get; init; } = SchemaName;
    public DateTimeOffset SavedAtUtc { get; init; }
    public List<SessionTouchPackedBatch> Batches { get; init; } = [];
}
