using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ansight.Host.Runtime.NativeProfiling;

internal sealed record NativeProfilingProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan? Timeout = null,
    TimeSpan? InterruptAfter = null,
    string? StandardInput = null);

internal sealed record NativeProfilingProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool WasInterrupted = false)
{
    public bool IsSuccess => ExitCode == 0 && !TimedOut;
}

internal interface INativeProfilingProcessRunner
{
    Task<NativeProfilingProcessResult> RunAsync(
        NativeProfilingProcessRequest request,
        Action<string>? outputReceived,
        CancellationToken cancellationToken);
}

internal sealed class NativeProfilingProcessRunner : INativeProfilingProcessRunner
{
    public async Task<NativeProfilingProcessResult> RunAsync(
        NativeProfilingProcessRequest request,
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
            RedirectStandardInput = request.StandardInput is not null,
            CreateNoWindow = true
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) => AppendLine(
            eventArgs.Data,
            standardOutput,
            outputReceived);
        process.ErrorDataReceived += (_, eventArgs) => AppendLine(
            eventArgs.Data,
            standardError,
            outputReceived);

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
        var wasInterrupted = false;
        try
        {
            if (request.StandardInput is not null)
            {
                process.StandardInput.Write(request.StandardInput);
                process.StandardInput.Close();
            }

            var exitTask = process.WaitForExitAsync(linkedCancellation.Token);
            if (request.InterruptAfter is not null)
            {
                var interruptTask = Task.Delay(request.InterruptAfter.Value, linkedCancellation.Token);
                var completedTask = await Task.WhenAny(exitTask, interruptTask).ConfigureAwait(false);
                if (completedTask == interruptTask
                    && !interruptTask.IsCanceled
                    && !process.HasExited)
                {
                    wasInterrupted = TryInterrupt(process);
                }
            }

            await exitTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                                                 && timeoutCancellation?.IsCancellationRequested == true)
        {
            timedOut = true;
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return new NativeProfilingProcessResult(
            process.ExitCode,
            standardOutput.ToString(),
            standardError.ToString(),
            timedOut,
            wasInterrupted);
    }

    private static void AppendLine(
        string? line,
        StringBuilder builder,
        Action<string>? outputReceived)
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

    private static bool TryInterrupt(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            if (!process.HasExited)
            {
                return kill(process.Id, InterruptSignal) == 0;
            }
        }
        catch (InvalidOperationException)
        {
        }

        return false;
    }

    private const int InterruptSignal = 2;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int processId, int signal);
}
