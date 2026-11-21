using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Ansight.Host.Workspaces.Execution;

internal sealed class ElectronTestVideoRecorder(AppToolService tools, string sessionId) : ITestVideoRecorder
{
    public async Task<ITestVideoRecording> StartAsync(TestVideoRecordingRequest request, CancellationToken cancellationToken)
    {
        await RunFfmpegAsync(["-version"], cancellationToken).ConfigureAwait(false);
        var result = await CallAsync("start", new(), cancellationToken).ConfigureAwait(false);
        var id = result["recordingId"]?.GetValue<string>() ?? throw new IOException("Electron recorder returned no recording ID.");
        return new Recording(this, id, request.OutputPath);
    }

    private async Task<JsonObject> CallAsync(string action, JsonObject args, CancellationToken token)
    {
        var response = await tools.CallAsync(sessionId, "electron.video." + action, args, token).ConfigureAwait(false);
        if (!response.Success || response.Envelope?.Payload?["success"]?.GetValue<bool>() != true
            || response.Envelope.Payload["result"] is not JsonObject result)
        {
            throw new IOException(response.Message);
        }
        return result;
    }

    private static async Task RunFfmpegAsync(string[] arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true,
            RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        using var process = Process.Start(start) ?? throw new IOException("Could not start ffmpeg for Electron video.");
        var error = process.StandardError.ReadToEndAsync(token);
        var output = process.StandardOutput.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token).ConfigureAwait(false); }
        catch { if (!process.HasExited) { process.Kill(entireProcessTree: true); } throw; }
        await output.ConfigureAwait(false);
        var message = await error.ConfigureAwait(false);
        if (process.ExitCode != 0) { throw new IOException("Electron video conversion failed: " + message); }
    }

    private sealed class Recording : ITestVideoRecording
    {
        private readonly ElectronTestVideoRecorder owner;
        private readonly string id;
        private readonly string outputPath;
        private readonly CancellationTokenSource monitoring = new();
        private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock gate = new();
        private Task<TestVideoRecordingCompletion>? stop;
        private string? monitorError;
        public string Source => "electron.webContents";
        public Task Completion => completed.Task;

        internal Recording(ElectronTestVideoRecorder owner, string id, string outputPath)
        {
            this.owner = owner; this.id = id; this.outputPath = outputPath;
            _ = MonitorAsync();
        }
        private async Task MonitorAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(1000, monitoring.Token).ConfigureAwait(false);
                    var result = await owner.CallAsync("status", new(), monitoring.Token).ConfigureAwait(false);
                    if (result["recordingId"]?.GetValue<string>() != id || result["state"]?.GetValue<string>() != "recording")
                    { throw new IOException(result["error"]?.GetValue<string>() ?? "Electron recording ended unexpectedly."); }
                }
            }
            catch (OperationCanceledException) when (monitoring.IsCancellationRequested) { }
            catch (Exception e) { monitorError = e.Message; completed.TrySetResult(); }
        }
        public Task<TestVideoRecordingCompletion> StopAsync()
        {
            lock (gate) { return stop ??= StopCoreAsync(); }
        }
        private async Task<TestVideoRecordingCompletion> StopCoreAsync()
        {
            monitoring.Cancel();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try
            {
                if (monitorError is not null) { throw new IOException(monitorError); }
                var result = await owner.CallAsync("stop", new() { ["recordingId"] = id }, timeout.Token).ConfigureAwait(false);
                var artifact = result["artifactPath"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(artifact) || !File.Exists(artifact)) { throw new IOException("Electron video transfer produced no file."); }
                await using (var input = File.OpenRead(artifact))
                {
                    var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, timeout.Token).ConfigureAwait(false));
                    if (!string.Equals(hash, result["sha256"]?.GetValue<string>(), StringComparison.Ordinal))
                    { throw new IOException("Electron video transfer checksum mismatch."); }
                }
                await RunFfmpegAsync(["-nostdin", "-v", "error", "-y", "-i", artifact, "-an", "-c:v", "libx264",
                    "-pix_fmt", "yuv420p", "-movflags", "+faststart", outputPath], timeout.Token).ConfigureAwait(false);
                var started = DateTimeOffset.Parse(result["startedAtUtc"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
                var ended = DateTimeOffset.Parse(result["endedAtUtc"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
                return new(true, CapturedDuration: ended - started);
            }
            catch (Exception e)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await owner.CallAsync("cancel", new() { ["recordingId"] = id }, cleanup.Token).ConfigureAwait(false); } catch { }
                return new(false, e.Message);
            }
            finally { completed.TrySetResult(); }
        }
    }
}
