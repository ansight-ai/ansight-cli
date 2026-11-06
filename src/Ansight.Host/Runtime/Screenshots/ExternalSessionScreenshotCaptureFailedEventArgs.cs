namespace Ansight.Host.Runtime.Screenshots;

using System.Text.Json.Nodes;

internal sealed class ExternalSessionScreenshotCaptureFailedEventArgs : EventArgs
{
    public ExternalSessionScreenshotCaptureFailedEventArgs(
        string sessionId,
        string reason)
    {
        this.SessionId = sessionId;
        this.Reason = reason;
    }

    public string SessionId { get; }
    public string Reason { get; }
}
