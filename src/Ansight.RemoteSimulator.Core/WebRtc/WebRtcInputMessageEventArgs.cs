namespace Ansight.RemoteSimulator.Core.WebRtc;

public sealed class WebRtcInputMessageEventArgs : EventArgs
{
    public WebRtcInputMessageEventArgs(string message)
    {
        Message = message;
    }

    public string Message { get; }
}
