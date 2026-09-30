using Ansight.Adb;

namespace Ansight.Adb.Emulator;

internal sealed class AdbLauncherProcessRunner : IProcessRunner
{
    private readonly IAdbProcessLauncher processLauncher;

    public AdbLauncherProcessRunner(IAdbProcessLauncher processLauncher)
    {
        this.processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
    }

    public async Task<AdbCommandResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        await using var process = Start(executablePath, arguments);
        process.StandardInput.Dispose();
        try
        {
            using var standardOutputReader = new StreamReader(process.StandardOutput);
            using var standardErrorReader = new StreamReader(process.StandardError);
            var standardOutputTask = standardOutputReader.ReadToEndAsync(cancellationToken);
            var standardErrorTask = standardErrorReader.ReadToEndAsync(cancellationToken);
            var exitCode = await process.Completion
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);
            return new AdbCommandResult(
                exitCode,
                standardOutputTask.Result,
                standardErrorTask.Result);
        }
        catch (OperationCanceledException)
        {
            process.Terminate(force: true);
            throw;
        }
    }

    public int Launch(string executablePath, IReadOnlyList<string> arguments)
    {
        var process = Start(executablePath, arguments);
        process.StandardInput.Dispose();
        _ = ObserveLaunchedProcessAsync(process);
        return process.ProcessId;
    }

    private IAdbProcess Start(string executablePath, IReadOnlyList<string> arguments)
        => processLauncher.Start(new AdbProcessStartRequest(
            executablePath,
            arguments,
            Path.GetDirectoryName(executablePath)));

    private static async Task ObserveLaunchedProcessAsync(IAdbProcess process)
    {
        await using (process)
        {
            try
            {
                var standardOutputTask = process.StandardOutput.CopyToAsync(Stream.Null);
                var standardErrorTask = process.StandardError.CopyToAsync(Stream.Null);
                await process.Completion.ConfigureAwait(false);
                await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);
            }
            catch (Exception suppressedException)
            {
                System.Diagnostics.Trace.TraceWarning(suppressedException.ToString());
            }
        }
    }
}
