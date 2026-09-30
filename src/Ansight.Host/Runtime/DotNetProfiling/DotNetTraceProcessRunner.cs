using System.Diagnostics;

namespace Ansight.Host.Runtime.DotNetProfiling;

internal sealed class DotNetTraceProcessRunner : IDotNetTraceProcessRunner
{
    public async Task<DotNetTraceProcessResult> RunAsync(
        DotNetTraceProcessRequest request,
        Action<string>? outputReceived,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

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

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) => AppendLine(eventArgs.Data, standardOutput, outputReceived);
        process.ErrorDataReceived += (_, eventArgs) => AppendLine(eventArgs.Data, standardError, outputReceived);

        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start '{request.FileName}'.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCancellation = request.Timeout is null
            ? null
            : new CancellationTokenSource(request.Timeout.Value);
        using var linkedCancellation = timeoutCancellation is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                                                 && timeoutCancellation?.IsCancellationRequested == true)
        {
            timedOut = true;
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        return new DotNetTraceProcessResult(
            process.ExitCode,
            standardOutput.ToString(),
            standardError.ToString(),
            timedOut);
    }

    private static void AppendLine(string? line, StringBuilder builder, Action<string>? outputReceived)
    {
        if (line is null)
        {
            return;
        }

        lock (builder)
        {
            builder.AppendLine(line);
        }

        outputReceived?.Invoke(line);
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
        }
    }
}
