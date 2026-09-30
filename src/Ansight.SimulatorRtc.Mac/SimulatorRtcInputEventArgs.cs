namespace Ansight.SimulatorRtc.Mac;

public sealed class SimulatorRtcInputEventArgs : EventArgs
{
    public SimulatorRtcInputEventArgs(string message)
    {
        Message = message;
    }

    public string Message { get; }
}
