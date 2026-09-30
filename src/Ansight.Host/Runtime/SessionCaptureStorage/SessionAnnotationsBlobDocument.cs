namespace Ansight.Host.Runtime.SessionCaptureStorage;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using Ansight.Host;

internal sealed class SessionAnnotationsBlobDocument
{
    public const string SchemaName = "ansight.session-annotations.v1";

    public string Schema { get; init; } = SchemaName;
    public required DateTimeOffset SavedAtUtc { get; init; }
    public required IReadOnlyList<SessionAnnotation> Annotations { get; init; }
}
