namespace Ansight.Host.Runtime.Screenshots;

using System.Text.Json.Nodes;

internal sealed class ExternalSessionScreenshotCaptureFailedEventArgs(
    string sessionId,
    string reason) : EventArgs
{
    public string SessionId { get; } = sessionId;

    public string Reason { get; } = reason;
}
