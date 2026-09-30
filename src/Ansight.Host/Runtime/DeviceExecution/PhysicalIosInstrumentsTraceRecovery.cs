using System.IO.Compression;
using Ansight.Host.Runtime.NativeProfiling;

namespace Ansight.Host.Runtime.DeviceExecution;

internal static class PhysicalIosInstrumentsTraceRecovery
{
    private const string ProcessTableXPath =
        "/trace-toc/run[@number=\"1\"]/data/table[@schema=\"activity-monitor-process-live\"]";
    private const string RawProcessTableXPath =
        "/trace-toc/run[@number=\"1\"]/data/table[@schema=\"sysmon-process\"]";

    internal static async Task<PhysicalIosInstrumentsMetricResult> IngestAsync(
        IRuntimeState state, string sessionId, string archivePath, string temporaryRoot,
        DateTimeOffset startedUtc, int processId, CancellationToken cancellationToken = default,
        INativeProfilingProcessRunner? processRunner = null, string? exportedSamplesPath = null)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Rebuilding an Instruments trace requires macOS and Xcode.");

        var directory = Path.Combine(temporaryRoot, "ios-instruments-recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            ZipFile.ExtractToDirectory(archivePath, directory);
            var trace = Path.Combine(directory, "capture.trace");
            if (!PhysicalIosInstrumentsCapture.HasUsableTrace(trace))
                throw new InvalidDataException("The retained Instruments archive has no usable trace.");

            var xmlPath = Path.Combine(directory, "activity-monitor-process.xml");
            var runner = processRunner ?? new NativeProfilingProcessRunner();
            foreach (var xpath in new[] { ProcessTableXPath, RawProcessTableXPath })
            {
                // Instruments can crash while modeling its derived live table.
                // The raw sysmon table carries the same per-process counters.
                if (File.Exists(xmlPath)) File.Delete(xmlPath);
                var result = await runner.RunAsync(
                    new NativeProfilingProcessRequest("/usr/bin/xcrun",
                        ["xctrace", "export", "--input", trace, "--xpath", xpath,
                            "--output", xmlPath], directory, Timeout: TimeSpan.FromSeconds(20)),
                    null, cancellationToken).ConfigureAwait(false);
                if (!result.IsSuccess || !File.Exists(xmlPath) || new FileInfo(xmlPath).Length == 0)
                {
                    if (xpath == ProcessTableXPath) continue;
                    throw new IOException($"The retained Instruments trace could not export process samples " +
                        $"(exit {result.ExitCode}): {result.StandardError.Trim()}");
                }
                PhysicalIosInstrumentsMetricResult metrics;
                try
                {
                    metrics = PhysicalIosInstrumentsMetrics.Ingest(state, sessionId, xmlPath, startedUtc, processId);
                }
                catch (Exception exception) when (xpath == ProcessTableXPath
                    && (exception is InvalidDataException or System.Xml.XmlException))
                {
                    continue;
                }
                if (exportedSamplesPath is not null)
                    File.Copy(xmlPath, exportedSamplesPath, overwrite: true);
                return metrics;
            }
            throw new InvalidDataException("The retained Instruments trace has no usable process samples.");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }
}
