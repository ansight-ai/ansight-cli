namespace Ansight.Host.Runtime.SessionCaptureStorage;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using Ansight.Host;

internal sealed record PendingImageWrite(
    string AppId,
    string SessionId,
    DateTimeOffset CapturedAtUtc,
    string Format,
    int Width,
    int Height,
    int Quality,
    ReadOnlyMemory<byte> Bytes,
    bool AllowDuplicate,
    TaskCompletionSource<SessionImageFrame?> Completion);
