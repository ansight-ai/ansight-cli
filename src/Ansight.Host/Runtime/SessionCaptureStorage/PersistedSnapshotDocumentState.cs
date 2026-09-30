namespace Ansight.Host.Runtime.SessionCaptureStorage;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using Ansight.Host;

internal sealed class PersistedSnapshotDocumentState
{
    public required string FilePath { get; init; }

    public required DateTimeOffset CapturedAtUtc { get; init; }

    public required string ContentFingerprint { get; init; }

    public object? PersistedSource { get; set; }
}
