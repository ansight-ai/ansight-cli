namespace Ansight.Host.Runtime.SessionCaptureStorage;

using Ansight.Host;

internal sealed class SessionImagesAppendDocument
{
    public const string SchemaName = "ansight.session-images-append.v1";

    public string Schema { get; init; } = SchemaName;
    public required DateTimeOffset SavedAtUtc { get; init; }
    public required IReadOnlyList<SessionImageFrame> Images { get; init; }
}
