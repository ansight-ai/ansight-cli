namespace Ansight.Host.Runtime.SessionCaptureStorage;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using Ansight.Host;

internal sealed class SessionVisualTreeSnapshotDocument
{
    public const string SchemaName = "ansight.session-visual-tree-snapshot.v2";

    public string Schema { get; init; } = SchemaName;
    public required DateTimeOffset SavedAtUtc { get; init; }
    public required SessionVisualTreeSnapshotHeader Header { get; init; }
    public required SessionVisualTreeSnapshot Snapshot { get; init; }
}
