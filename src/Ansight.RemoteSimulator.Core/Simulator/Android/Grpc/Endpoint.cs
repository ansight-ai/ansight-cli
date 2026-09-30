namespace Ansight.RemoteSimulator.Core.Simulator.Android.Grpc;

internal readonly record struct Endpoint(int Port)
{
    private const int GrpcPortOffset = 3000;

    public static Endpoint Resolve(string deviceSerial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceSerial);
        const string emulatorPrefix = "emulator-";
        if (!deviceSerial.StartsWith(emulatorPrefix, StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(deviceSerial.AsSpan(emulatorPrefix.Length), out var consolePort)
            || consolePort is < 1024 or > 62535)
        {
            throw new NotSupportedException(
                $"Android Emulator gRPC is unavailable for device serial '{deviceSerial}'.");
        }

        return new Endpoint(consolePort + GrpcPortOffset);
    }
}
