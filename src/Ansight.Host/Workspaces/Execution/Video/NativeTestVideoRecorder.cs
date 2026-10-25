using System.Diagnostics;
using System.Runtime.InteropServices;
using Ansight.Adb;
using Ansight.Infrastructure.Preferences;
using Ansight.SimCtl;

namespace Ansight.Host.Workspaces.Execution;

internal sealed class NativeTestVideoRecorder(IUserPreferences preferences) : ITestVideoRecorder
{
    public async Task<ITestVideoRecording> StartAsync(TestVideoRecordingRequest request, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            throw new NotSupportedException("Test video recording v1 requires a macOS or Linux host.");
        var start = new ProcessStartInfo { UseShellExecute = false, RedirectStandardError = true,
            RedirectStandardOutput = true, CreateNoWindow = true };
        string source;
        if (request.Platform == DevicePlatforms.Ios && request.IsVirtual && OperatingSystem.IsMacOS())
        {
            var tool = await SimCtlToolLocator.ResolveAsync(preferences.XcodePath, cancellationToken).ConfigureAwait(false);
            if (!tool.IsFound) throw new IOException(tool.Message);
            source = "simctl";
            start.FileName = tool.XcrunPath;
            start.Environment["DEVELOPER_DIR"] = tool.DeveloperDirectory;
            AddArguments(start, ["simctl", "io", request.DeviceIdentifier, "recordVideo", "--codec=h264", request.OutputPath]);
        }
        else if (request.Platform == DevicePlatforms.Android)
        {
            var tool = await ScrcpyToolLocator.ResolveAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!tool.IsFound) throw new IOException(tool.Message);
            var adb = AdbToolLocator.Resolve(preferences.AdbPath);
            if (!adb.IsFound) throw new IOException(adb.Message);
            source = "scrcpy";
            start.FileName = tool.ExecutablePath;
            start.Environment["ADB"] = adb.AdbPath;
            start.Environment["SCRCPY_SERVER_PATH"] = tool.ServerPath;
            AddArguments(start, ["--serial", request.DeviceIdentifier, "--no-audio", "--no-playback",
                "--no-window", "--no-control", "--video-codec=h264", "--max-fps=24", "--max-size=1280",
                "--video-bit-rate=1200000", "--record-format=mp4", "--record", request.OutputPath]);
        }
        else throw new NotSupportedException("Test video recording v1 supports iOS Simulator and Android devices/emulators.");

        return await NativeTestVideoRecording.StartAsync(start, source, cancellationToken).ConfigureAwait(false);
    }

    private static void AddArguments(ProcessStartInfo start, IEnumerable<string> arguments)
    {
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
    }
}

internal sealed class NativeTestVideoRecording : ITestVideoRecording
{
    private readonly Process process;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock gate = new();
    private readonly Queue<string> diagnostics = new();
    private Task<TestVideoRecordingCompletion>? stopTask;
    private long startedTimestamp;
    public string Source { get; }

    private NativeTestVideoRecording(ProcessStartInfo start, string source)
    {
        Source = source;
        process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += ReceiveOutput;
        process.ErrorDataReceived += ReceiveOutput;
        process.Exited += (_, _) => ready.TrySetException(new IOException($"The {Source} recorder exited before becoming ready. {DiagnosticText()}"));
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
            if (!started) throw new IOException($"Could not start the {source} video recorder.");
            recording.process.BeginOutputReadLine();
            recording.process.BeginErrorReadLine();
            await recording.ready.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (recording.process.HasExited) throw new IOException($"The {source} video recorder exited during startup.");
            return recording;
        }
        catch
        {
            if (started) await recording.StopAsync().ConfigureAwait(false);
            else recording.process.Dispose();
            throw;
        }
    }

    private void ReceiveOutput(object sender, DataReceivedEventArgs args)
    {
        if (args.Data is not { } line) return;
        lock (gate)
        {
            diagnostics.Enqueue(line.Length > 1024 ? line[..1024] : line);
            while (diagnostics.Count > 8) diagnostics.Dequeue();
        }
        if (line.Contains("Recording started", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.CompareExchange(ref startedTimestamp, Stopwatch.GetTimestamp(), 0);
            ready.TrySetResult();
        }
    }

    private string DiagnosticText()
    {
        lock (gate) return string.Join("\n", diagnostics);
    }

    public Task<TestVideoRecordingCompletion> StopAsync()
    {
        lock (gate) return stopTask ??= StopCoreAsync();
    }

    private async Task<TestVideoRecordingCompletion> StopCoreAsync()
    {
        // Never inherit the test's cancellation: both recorders need SIGINT to write the MP4 index.
        try
        {
            var exitedEarly = process.HasExited;
            var capturedDuration = startedTimestamp == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(startedTimestamp);
            if (!exitedEarly && kill(process.Id, 2) != 0 && !process.HasExited)
                throw new IOException("Could not interrupt the video recorder to finalize its MP4.");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            return !exitedEarly && process.ExitCode == 0
                ? new(true, CapturedDuration: capturedDuration)
                : new(false, $"The recorder {(exitedEarly ? "stopped before the test ended" : $"exited with code {process.ExitCode}")}. {DiagnosticText()}");
        }
        catch (Exception exception)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception) { /* Preserve the finalization error. */ }
            return new(false, $"Video finalization failed: {exception.Message}");
        }
        finally { process.Dispose(); }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int processId, int signal);
}
