namespace Ansight.RemoteSimulator.Core.Simulator.Android.Grpc.Audio;

public sealed class AndroidAudioEndpointException : Exception
{
    public AndroidAudioEndpointException(string code, string message) : base(message)
    {
        this.Code = code;
    }

    public string Code { get; }
}
