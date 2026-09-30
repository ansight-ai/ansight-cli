namespace Ansight.RemoteSimulator.Core.Simulator.Android.Grpc.Audio;

/// <summary>A discovered process-bound local endpoint. Authentication is never included in diagnostics.</summary>
public sealed class AndroidAudioEndpoint
{
    internal AndroidAudioEndpoint(string serial, int processId, long processStartTicks, int port, string token)
    {
        Serial = serial;
        ProcessId = processId;
        ProcessStartTicks = processStartTicks;
        Port = port;
        Token = token;
    }

    public string Serial { get; }
    public int ProcessId { get; }
    public long ProcessStartTicks { get; }
    public int Port { get; }
    public Uri Address => new($"http://127.0.0.1:{Port}");
    internal string Token { get; }

    internal bool Matches(AndroidAudioEndpoint other)
        => Serial == other.Serial && ProcessId == other.ProcessId
           && ProcessStartTicks == other.ProcessStartTicks && Port == other.Port && Token == other.Token;

    public override string ToString() => $"{Serial} (process {ProcessId}, port {Port})";
}

public sealed class AndroidAudioEndpointException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
