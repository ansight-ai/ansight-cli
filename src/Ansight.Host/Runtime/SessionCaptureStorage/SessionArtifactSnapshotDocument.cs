namespace Ansight.Host.Runtime.SessionCaptureStorage;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using Ansight.Host;

internal sealed class SessionArtifactSnapshotDocument
{
    public const string SchemaName = "ansight.session-artifact-snapshot.v1";

    public string Schema { get; init; } = SchemaName;
    public required DateTimeOffset SavedAtUtc { get; init; }
    public required SessionArtifactSnapshotHeader Header { get; init; }
    public required SessionArtifactSnapshot Snapshot { get; init; }
}
