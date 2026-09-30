namespace Ansight.Host.Runtime.SessionCaptureStorage;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using Ansight.Host;

internal sealed class PersistedSnapshotDocumentCollectionState
{
    public Dictionary<string, PersistedSnapshotDocumentState> DocumentsBySnapshotId { get; } = new(StringComparer.Ordinal);

    public HashSet<string> FilePaths { get; } = new(StringComparer.Ordinal);

    public object? PersistedSource { get; set; }

    public int PersistedSnapshotCount { get; set; }
}
