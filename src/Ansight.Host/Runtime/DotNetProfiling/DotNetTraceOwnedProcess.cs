using System.Diagnostics;

namespace Ansight.Host.Runtime.DotNetProfiling;

internal sealed class DotNetTraceOwnedProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly CancellationTokenSource outputCancellation = new();
    private readonly Task outputTask;
    private readonly Task errorTask;
    private bool disposed;

    private DotNetTraceOwnedProcess(
        Process process,
        Action<string>? outputReceived)
    {
        this.process = process;
        outputTask = PumpAsync(process.StandardOutput, outputReceived, outputCancellation.Token);
        errorTask = PumpAsync(process.StandardError, outputReceived, outputCancellation.Token);
    }

    public int ProcessId => process.Id;

    public Task Completion => process.WaitForExitAsync();

    public static DotNetTraceOwnedProcess Start(
        DotNetTraceProcessRequest request,
        Action<string>? outputReceived)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (request.EnvironmentVariables is not null)
        {
            foreach (var environmentVariable in request.EnvironmentVariables)
            {
                startInfo.Environment[environmentVariable.Key] = environmentVariable.Value;
            }
        }

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Could not start '{request.FileName}'.");
        }

        return new DotNetTraceOwnedProcess(process, outputReceived);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        TryKill();
        try
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
        }

        outputCancellation.Cancel();
        try
        {
            await Task.WhenAll(outputTask, errorTask);
        }
        catch (OperationCanceledException)
        {
        }

        outputCancellation.Dispose();
        process.Dispose();
    }

    private void TryKill()
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
        }
    }

    private static async Task PumpAsync(
        StreamReader reader,
        Action<string>? outputReceived,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return;
            }

            outputReceived?.Invoke(line);
        }
    }
}
