using Ansight.Adb;
using Ansight.RemoteSimulator.Core.Simulator.Android;

namespace Ansight.Host.Tests.Unit.RemoteSimulator;

internal sealed class FakeAndroidEmulatorClient : IAndroidEmulatorClient
{
    public IReadOnlyList<AdbDevice> Devices { get; init; } = [];

    public Dictionary<string, AdbCommandResult> Responses { get; } = new(StringComparer.Ordinal);

    public List<IReadOnlyList<string>> Commands { get; } = [];

    public List<RecordedAdbStandardInputCommand> StandardInputCommands { get; } = [];

    public Task<IReadOnlyList<AdbDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Devices);

    public Task<byte[]> CaptureScreenshotPngAsync(
        string deviceSerial,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

    public Task<AdbCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        Commands.Add(arguments.ToArray());
        var key = string.Join(' ', arguments);
        return Task.FromResult(Responses.TryGetValue(key, out var result)
            ? result
            : new AdbCommandResult(0, string.Empty, string.Empty));
    }

    public Task<AdbCommandResult> RunWithStandardInputAsync(
        IReadOnlyList<string> arguments,
        string standardInput,
        CancellationToken cancellationToken = default)
    {
        StandardInputCommands.Add(new RecordedAdbStandardInputCommand(
            arguments.ToArray(),
            standardInput));
        var key = string.Join(' ', arguments);
        return Task.FromResult(Responses.TryGetValue(key, out var result)
            ? result
            : new AdbCommandResult(0, string.Empty, string.Empty));
    }
}

internal sealed record RecordedAdbStandardInputCommand(
    IReadOnlyList<string> Arguments,
    string StandardInput);
