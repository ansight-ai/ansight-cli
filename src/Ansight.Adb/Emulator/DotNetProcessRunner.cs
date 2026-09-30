using System.Diagnostics;
using Ansight.Adb;

namespace Ansight.Adb.Emulator;

internal sealed class DotNetProcessRunner : IProcessRunner
{
    public async Task<AdbCommandResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        using var process = CreateProcess(executablePath, arguments, redirectOutput: true);
        if (!process.Start())
        {
            return new AdbCommandResult(-1, string.Empty, "Android Emulator could not be started.");
        }

        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            return new AdbCommandResult(process.ExitCode, outputTask.Result, errorTask.Result);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    public int Launch(string executablePath, IReadOnlyList<string> arguments)
    {
        using var process = CreateProcess(executablePath, arguments, redirectOutput: false);
        if (!process.Start())
        {
            throw new InvalidOperationException("Android Emulator could not be started.");
        }

        return process.Id;
    }

    private static Process CreateProcess(
        string executablePath,
        IReadOnlyList<string> arguments,
        bool redirectOutput)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = redirectOutput,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return new Process { StartInfo = startInfo };
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // This exception is an expected fallback for the best-effort operation.
        }
    }
}
