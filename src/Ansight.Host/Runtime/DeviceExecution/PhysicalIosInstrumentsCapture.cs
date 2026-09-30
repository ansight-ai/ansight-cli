using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.NativeProfiling;

namespace Ansight.Host.Runtime.DeviceExecution;

/// <summary>Records iOS activity alongside a host or SDK session, ingesting only the app's PID.</summary>
internal sealed class PhysicalIosInstrumentsCapture : IAsyncDisposable
{
    internal const string ActivityMonitorTemplate = "Activity Monitor";
    internal const string TimeProfilerTemplate = "Time Profiler";
    private readonly string sessionId;
    private readonly string deviceId;
    private readonly int processId;
    private readonly string template;
    private readonly string workDirectory;
    private readonly IRuntimeState state;
    private readonly Process process;
    private readonly Task<string> standardOutput;
    private readonly Task<string> standardError;
    private readonly DateTimeOffset startedUtc;
    private DateTimeOffset? processingStartedUtc;
    private int stopped;

    private PhysicalIosInstrumentsCapture(string sessionId, string deviceId, int processId,
        string template, string workDirectory, IRuntimeState state, Process process)
    {
        this.sessionId = sessionId;
        this.deviceId = deviceId;
        this.processId = processId;
        this.template = template;
        this.workDirectory = workDirectory;
        this.state = state;
        this.process = process;
        startedUtc = DateTimeOffset.UtcNow;
        standardOutput = process.StandardOutput.ReadToEndAsync();
        standardError = process.StandardError.ReadToEndAsync();
    }

    internal static Task<PhysicalIosInstrumentsCapture> StartAsync(string sessionId, string deviceId,
        string processIdentity, string temporaryRoot, IRuntimeState state)
    {
        if (!TryParseProcessId(processIdentity, out var processId))
            throw new ArgumentException("The watched iPhone app has no valid process ID.", nameof(processIdentity));
        return StartAsync(sessionId, deviceId, processId, temporaryRoot, state);
    }

    internal static async Task<PhysicalIosInstrumentsCapture> StartAsync(string sessionId, string deviceId,
        int processId, string temporaryRoot, IRuntimeState state,
        string template = ActivityMonitorTemplate, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await StartOnceAsync(sessionId, deviceId, processId, temporaryRoot, state,
                    template, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception) when (attempt < 3
                && exception.Message.Contains("Cannot find process for provided pid", StringComparison.OrdinalIgnoreCase))
            {
                HostSessionEvents.Publish(state, sessionId, "instruments.attach.retry",
                    "host.instruments.attach.retry", $"PID {processId}, attempt {attempt + 1}");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<PhysicalIosInstrumentsCapture> StartOnceAsync(string sessionId, string deviceId,
        int processId, string temporaryRoot, IRuntimeState state,
        string template, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("iOS Instruments capture requires macOS and Xcode.");
        if (processId <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId));
        if (template is not (ActivityMonitorTemplate or TimeProfilerTemplate))
            throw new ArgumentOutOfRangeException(nameof(template));
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(temporaryRoot, "ios-instruments", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo("/usr/bin/xcrun")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in BuildRecordingArguments(template, deviceId, processId,
                     Path.Combine(directory, "capture.trace")))
            start.ArgumentList.Add(argument);
        Process? process = null;
        try
        {
            process = Process.Start(start)
                ?? throw new InvalidOperationException("xctrace did not start.");
            var capture = new PhysicalIosInstrumentsCapture(sessionId, deviceId, processId, template,
                directory, state, process);
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            if (process.HasExited)
                throw new InvalidOperationException($"xctrace could not attach to iOS process {processId}: "
                    + (await capture.standardError.ConfigureAwait(false)).Trim());
            capture.SetCaptureStatus("recording");
            HostSessionEvents.Publish(state, sessionId, "instruments.started",
                "host.instruments.started", processId.ToString(CultureInfo.InvariantCulture));
            return capture;
        }
        catch
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
                catch (InvalidOperationException) { }
            }
            process?.Dispose();
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            throw;
        }
    }

    internal static bool TryParseProcessId(string? identity, out int processId)
        => int.TryParse(identity?.Split(':', 2)[0], NumberStyles.None, CultureInfo.InvariantCulture,
            out processId) && processId > 0;

    internal static IReadOnlyList<string> BuildRecordingArguments(
        string template, string deviceId, int processId, string tracePath)
    {
        List<string> arguments = ["xctrace", "record", "--template", template, "--device", deviceId];
        // Store builds can be absent from xctrace's attachable-process list even
        // when CoreDevice and WDA report their PID. Activity Monitor can still
        // collect them device-wide; ingestion filters the resulting table by PID.
        if (template == ActivityMonitorTemplate)
            arguments.Add("--all-processes");
        else
        {
            arguments.Add("--attach");
            arguments.Add(processId.ToString(CultureInfo.InvariantCulture));
        }
        arguments.AddRange(["--time-limit", "600s", "--output", tracePath, "--no-prompt"]);
        return arguments;
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        try
        {
            processingStartedUtc = DateTimeOffset.UtcNow;
            SetCaptureStatus("processing", "stopping", 1);
            if (!process.HasExited)
            {
                if (kill(process.Id, 2) != 0)
                    throw new IOException("Could not stop the Instruments recording gracefully.");
                using var deadline = new CancellationTokenSource(template == TimeProfilerTemplate
                    ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(30));
                try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            await IngestAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            HostSessionEvents.Publish(state, sessionId, "instruments.failed", "host.instruments.failed",
                exception.Message);
            if (state.TryGetSessionSnapshot(sessionId, out var session))
            {
                var properties = session!.CustomProperties?.DeepClone().AsObject() ?? new JsonObject();
                var instruments = properties["instruments"] as JsonObject ?? new JsonObject();
                instruments["status"] = "failed";
                instruments["error"] = exception.Message;
                instruments["startedUtc"] = startedUtc;
                properties["instruments"] = instruments;
                state.SetSessionCustomProperties(sessionId, properties);
            }
        }
        finally
        {
            process.Dispose();
            try { Directory.Delete(workDirectory, recursive: true); }
            catch (IOException) { }
        }
    }

    private void SetCaptureStatus(string status, string? processingStage = null, int? processingStep = null)
    {
        if (!state.TryGetSessionSnapshot(sessionId, out var snapshot)) return;
        var properties = snapshot!.CustomProperties?.DeepClone().AsObject() ?? new JsonObject();
        properties["instruments"] = new JsonObject
        {
            ["status"] = status, ["template"] = template,
            ["targetScope"] = template == ActivityMonitorTemplate ? "all-processes" : "process",
            ["deviceId"] = deviceId, ["processId"] = processId, ["startedUtc"] = startedUtc,
            ["processingStage"] = processingStage, ["processingStep"] = processingStep,
            ["processingStepCount"] = processingStep.HasValue ? 5 : null,
            ["processingStartedUtc"] = processingStartedUtc,
            ["processingUpdatedUtc"] = processingStep.HasValue ? DateTimeOffset.UtcNow : null
        };
        state.SetSessionCustomProperties(sessionId, properties);
    }

    private async Task IngestAsync()
    {
        var output = await standardOutput.ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);
        var trace = Path.Combine(workDirectory, "capture.trace");
        if (!HasUsableTrace(trace))
            throw new IOException($"xctrace produced no usable trace (exit {process.ExitCode}): {error.Trim()} {output.Trim()}");

        SetCaptureStatus("processing", "preparing-trace", 2);
        var artifacts = Path.Combine(workDirectory, "artifacts");
        Directory.CreateDirectory(artifacts);
        var archive = Path.Combine(artifacts, "capture.trace.zip");
        ZipFile.CreateFromDirectory(trace, archive, CompressionLevel.Fastest, includeBaseDirectory: true);
        // xctrace can fail to export directly from the just-closed recording bundle.
        // A round trip through the retained archive also verifies that it can be opened later.
        var exportCopy = Path.Combine(workDirectory, "export-copy");
        ZipFile.ExtractToDirectory(archive, exportCopy);
        var exportTrace = Path.Combine(exportCopy, "capture.trace");
        SetCaptureStatus("processing", "exporting-metadata", 3);
        var toc = await ExportAsync(exportTrace, "--toc", "instruments-toc.xml").ConfigureAwait(false);
        SetCaptureStatus("processing", "exporting-process-samples", 3);
        var samples = template == ActivityMonitorTemplate && await ExportAsync(
            exportTrace,
            "/trace-toc/run[@number=\"1\"]/data/table[@schema=\"activity-monitor-process-live\"]",
            "activity-monitor-process.xml").ConfigureAwait(false);
        PhysicalIosInstrumentsMetricResult? metricResult = null;
        SetCaptureStatus("processing", "building-telemetry", 4);
        if (samples)
        {
            try
            {
                metricResult = PhysicalIosInstrumentsMetrics.Ingest(state, sessionId,
                    Path.Combine(artifacts, "activity-monitor-process.xml"), startedUtc, processId);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                HostSessionEvents.Publish(state, sessionId,
                    "instruments.metrics.failed", "host.instruments.metrics.failed", exception.Message);
            }
        }
        string? recoveryError = null;
        if (template == ActivityMonitorTemplate && metricResult is null)
        {
            SetCaptureStatus("processing", "recovering-process-samples", 4);
            try
            {
                metricResult = await PhysicalIosInstrumentsTraceRecovery.IngestAsync(state, sessionId,
                    archive, workDirectory, startedUtc, processId,
                    exportedSamplesPath: Path.Combine(artifacts, "activity-monitor-process.xml")).ConfigureAwait(false);
                samples = true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                recoveryError = exception.Message;
                HostSessionEvents.Publish(state, sessionId, "instruments.metrics.rebuild.failed",
                    "host.instruments.metrics.rebuild.failed", recoveryError);
            }
        }
        SetCaptureStatus("processing", "saving", 5);
        var completedUtc = DateTimeOffset.UtcNow;
        var metadata = new JsonObject
        {
            ["schema"] = "ansight.ios-instruments/v1",
            ["template"] = template,
            ["targetScope"] = template == ActivityMonitorTemplate ? "all-processes" : "process",
            ["deviceId"] = deviceId,
            ["processId"] = processId,
            ["startedUtc"] = startedUtc,
            ["completedUtc"] = completedUtc,
            ["processingStartedUtc"] = processingStartedUtc,
            ["recordExitCode"] = process.ExitCode,
            ["tableOfContentsExported"] = toc,
            ["processSamplesExported"] = samples,
            ["sampleMetricsIngested"] = metricResult is not null,
            ["metricRowCount"] = metricResult?.RowCount,
            ["processTableSchema"] = metricResult?.TableSchema,
            ["metricsRecovery"] = recoveryError is null ? null : new JsonObject
            {
                ["status"] = "failed", ["attemptedUtc"] = completedUtc, ["error"] = recoveryError
            }
        };
        await File.WriteAllTextAsync(Path.Combine(artifacts, "capture.json"),
            metadata.ToJsonString(new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
        var entries = Directory.GetFiles(artifacts).Select(path =>
        {
            var file = new FileInfo(path);
            return new SessionArtifactEntry
            {
                Name = file.Name,
                RootAlias = "host",
                RelativePath = file.Name,
                SnapshotRelativePath = file.Name,
                ArchiveRelativePath = file.Name,
                Kind = "file",
                SizeBytes = file.Length,
                FileExtension = file.Extension,
                LastModifiedUtc = file.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture)
            };
        }).ToArray();
        var snapshotId = Guid.NewGuid().ToString("N");
        var snapshot = new SessionArtifactSnapshot
        {
            SnapshotId = snapshotId,
            CapturedAtUtc = completedUtc,
            Source = "instruments",
            RootAlias = "host",
            RootPath = "host",
            RelativePath = "capture.trace.zip",
            Name = $"iOS {template} trace",
            Kind = "instruments-trace",
            ArtifactDirectoryName = "instruments-" + snapshotId,
            FileCount = entries.Length,
            ByteCount = entries.Sum(entry => entry.SizeBytes),
            Entries = entries
        };
        var result = state.AddSessionArtifactSnapshot(sessionId, snapshot, artifacts);
        if (!result.IsSuccess) throw new IOException(result.Message);
        if (state.TryGetSessionSnapshot(sessionId, out var session))
        {
            var properties = session!.CustomProperties?.DeepClone().AsObject() ?? new JsonObject();
            properties["instruments"] = new JsonObject
            {
                ["status"] = "ingested",
                ["snapshotId"] = snapshotId,
                ["template"] = template,
                ["targetScope"] = template == ActivityMonitorTemplate ? "all-processes" : "process",
                ["deviceId"] = deviceId,
                ["processId"] = processId,
                ["startedUtc"] = startedUtc,
                ["completedUtc"] = completedUtc,
                ["processingStartedUtc"] = processingStartedUtc,
                ["processingStage"] = "completed",
                ["processingStep"] = 5,
                ["processingStepCount"] = 5,
                ["tableOfContentsExported"] = toc,
                ["processSamplesExported"] = samples,
                ["sampleMetricsIngested"] = metricResult is not null,
                ["metricRowCount"] = metricResult?.RowCount,
                ["processTableSchema"] = metricResult?.TableSchema,
                ["metricsRecovery"] = recoveryError is null ? null : new JsonObject
                {
                    ["status"] = "failed", ["attemptedUtc"] = completedUtc, ["error"] = recoveryError
                }
            };
            state.SetSessionCustomProperties(sessionId, properties);
        }
        if (metricResult is not null)
            PhysicalIosInstrumentsMetrics.MarkIngested(state, sessionId, metricResult);
        HostSessionEvents.Publish(state, sessionId, "instruments.ingested", "host.instruments.ingested", snapshotId);
    }

    private async Task<bool> ExportAsync(string tracePath, string mode, string fileName)
    {
        var path = Path.Combine(workDirectory, "artifacts", fileName);
        var arguments = mode == "--toc"
            ? new[] { "xctrace", "export", "--input", tracePath, "--toc", "--output", path }
            : new[] { "xctrace", "export", "--input", tracePath, "--xpath", mode, "--output", path };
        var result = await new NativeProfilingProcessRunner().RunAsync(
            new NativeProfilingProcessRequest("/usr/bin/xcrun", arguments, workDirectory,
                Timeout: TimeSpan.FromSeconds(20)), null, CancellationToken.None).ConfigureAwait(false);
        if (result.IsSuccess && File.Exists(path) && new FileInfo(path).Length > 0) return true;
        if (File.Exists(path)) File.Delete(path);
        HostSessionEvents.Publish(state, sessionId, "instruments.export.failed",
            "host.instruments.export.failed", $"{fileName}: exit {result.ExitCode}");
        return false;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    internal static bool HasUsableTrace(string tracePath)
        => Directory.Exists(tracePath)
           && (HasData(Path.Combine(tracePath, "corespace"))
               || HasData(Path.Combine(tracePath, "instrument_data")));

    private static bool HasData(string directory)
        => Directory.Exists(directory)
           && Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
               .Any(file => new FileInfo(file).Length > 0);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int processId, int signal);
}
