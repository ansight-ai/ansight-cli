using System.Diagnostics;

namespace Ansight.SimCtl;

public sealed class DotNetSimCtlCommandRunner : ISimCtlCommandRunner
{
    public async Task<SimCtlCommandResult> RunAsync(
        SimCtlToolResolution toolResolution,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        using var process = CreateProcess(toolResolution, arguments);
        if (!process.Start())
        {
            return new SimCtlCommandResult(-1, string.Empty, "xcrun could not be started.");
        }

        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            return new SimCtlCommandResult(process.ExitCode, outputTask.Result, errorTask.Result);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static Process CreateProcess(
        SimCtlToolResolution toolResolution,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = toolResolution.XcrunPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["DEVELOPER_DIR"] = toolResolution.DeveloperDirectory;
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
