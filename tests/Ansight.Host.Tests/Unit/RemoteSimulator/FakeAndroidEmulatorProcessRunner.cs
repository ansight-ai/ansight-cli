using Ansight.Adb;
using Ansight.Adb.Emulator;

namespace Ansight.Host.Tests.Unit.RemoteSimulator;

internal sealed class FakeAndroidEmulatorProcessRunner : IProcessRunner
{
    public AdbCommandResult RunResult { get; init; } = new(0, string.Empty, string.Empty);

    public int ProcessId { get; init; } = 1;

    public string RunExecutablePath { get; private set; } = string.Empty;

    public IReadOnlyList<string> RunArguments { get; private set; } = [];

    public string LaunchExecutablePath { get; private set; } = string.Empty;

    public IReadOnlyList<string> LaunchArguments { get; private set; } = [];

    public Task<AdbCommandResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        RunExecutablePath = executablePath;
        RunArguments = arguments.ToArray();
        return Task.FromResult(RunResult);
    }

    public int Launch(string executablePath, IReadOnlyList<string> arguments)
    {
        LaunchExecutablePath = executablePath;
        LaunchArguments = arguments.ToArray();
        return ProcessId;
    }
}
