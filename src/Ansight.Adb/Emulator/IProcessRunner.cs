using Ansight.Adb;

namespace Ansight.Adb.Emulator;

internal interface IProcessRunner
{
    Task<AdbCommandResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);

    int Launch(string executablePath, IReadOnlyList<string> arguments);
}
