namespace Ansight.Host.Runtime.SessionCaptureStorage;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using Ansight.Host;

internal sealed class SessionImagesBlobDocument
{
    public const string SchemaName = "ansight.session-images.v2";

    public string Schema { get; init; } = SchemaName;
    public required DateTimeOffset SavedAtUtc { get; init; }
    public required IReadOnlyList<SessionImageFrame> Images { get; init; }
}
