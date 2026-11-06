using System.Diagnostics;
using System.Runtime.InteropServices;
using Ansight.Adb;
using Ansight.Infrastructure.Preferences;
using Ansight.SimCtl;

namespace Ansight.Host.Workspaces.Execution;

internal sealed class NativeTestVideoRecording : ITestVideoRecording
{
    private readonly Process process;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock gate = new();
    private readonly Queue<string> diagnostics = new();
    private Task<TestVideoRecordingCompletion>? stopTask;
    private long startedTimestamp;
    public string Source { get; }
    public Task Completion => exited.Task;

    private NativeTestVideoRecording(ProcessStartInfo start, string source)
    {
        Source = source;
        process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += ReceiveOutput;
        process.ErrorDataReceived += ReceiveOutput;
        process.Exited += (_, _) =>
        {
            ready.TrySetException(new IOException($"The {Source} recorder exited before becoming ready. {DiagnosticText()}"));
            exited.TrySetResult();
        };
    }

    internal static async Task<NativeTestVideoRecording> StartAsync(
        ProcessStartInfo start, string source, CancellationToken cancellationToken)
    {
        var recording = new NativeTestVideoRecording(start, source);
        var started = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            started = recording.process.Start();

            if (!started)
            {
                throw new IOException($"Could not start the {source} video recorder.");
            }

            recording.process.BeginOutputReadLine();
            recording.process.BeginErrorReadLine();
            await recording.ready.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (recording.process.HasExited)
            {
                throw new IOException($"The {source} video recorder exited during startup.");
            }

            return recording;
        }
        catch
        {
            if (started)
            {
                await recording.StopAsync().ConfigureAwait(false);
            }
            else
            {
                recording.process.Dispose();
            }

            throw;
        }
    }

    private void ReceiveOutput(object sender, DataReceivedEventArgs args)
    {
        if (args.Data is not { } line)
        {
            return;
        }

        lock (gate)
        {
            diagnostics.Enqueue(line.Length > 1024 ? line[..1024] : line);

            while (diagnostics.Count > 8)
            {
                diagnostics.Dequeue();
            }
        }

        if (line.Contains("Recording started", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.CompareExchange(ref startedTimestamp, Stopwatch.GetTimestamp(), 0);
            ready.TrySetResult();
        }
    }

    private string DiagnosticText()
    {
        lock (gate)
        {
            return string.Join("\n", diagnostics);
        }
    }

    public Task<TestVideoRecordingCompletion> StopAsync()
    {
        lock (gate)
        {
            return stopTask ??= StopCoreAsync();
        }
    }

    private async Task<TestVideoRecordingCompletion> StopCoreAsync()
    {
        // Never inherit the test's cancellation: both recorders need SIGINT to write the MP4 index.
        try
        {
            var exitedEarly = process.HasExited;
            var capturedDuration = startedTimestamp == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(startedTimestamp);

            if (!exitedEarly && kill(process.Id, 2) != 0 && !process.HasExited)
            {
                throw new IOException("Could not interrupt the video recorder to finalize its MP4.");
            }

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);

            return !exitedEarly && process.ExitCode == 0
                ? new(true, CapturedDuration: capturedDuration)
                : new(false, $"The recorder {(exitedEarly ? "stopped before the test ended" : $"exited with code {process.ExitCode}")}. {DiagnosticText()}");
        }
        catch (Exception exception)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                /* Preserve the finalization error. */
            }

            return new(false, $"Video finalization failed: {exception.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int processId, int signal);
}
