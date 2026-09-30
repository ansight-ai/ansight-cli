using System.ComponentModel.Composition;
using Ansight.Host.Runtime.NativeProfiling;

namespace Ansight.Host.Profiling;

[Export(typeof(NativeProfilingService))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class NativeProfilingService : IDisposable
{
    private readonly NativeProfilingEngine profiling;

    [ImportingConstructor]
    internal NativeProfilingService(IApplicationPaths applicationPaths)
    {
        profiling = new NativeProfilingEngine(
            applicationPaths ?? throw new ArgumentNullException(nameof(applicationPaths)));
    }

    public async Task<NativeProfilingToolchain> GetToolchainAsync(
        string platform,
        CancellationToken cancellationToken = default)
    {
        var toolchain = await profiling.GetToolchainAsync(platform, cancellationToken)
            .ConfigureAwait(false);
        return new NativeProfilingToolchain(
            toolchain.Platform,
            toolchain.Engine,
            toolchain.IsAvailable,
            toolchain.CaptureToolPath,
            toolchain.CaptureToolVersion,
            toolchain.SupportingToolVersion,
            toolchain.Message);
    }

    public async Task<NativeProfilingToolchain> GetProcessSampleToolchainAsync(
        CancellationToken cancellationToken = default)
    {
        var toolchain = await profiling.GetProcessSampleToolchainAsync(cancellationToken)
            .ConfigureAwait(false);
        return new NativeProfilingToolchain(
            toolchain.Platform,
            toolchain.Engine,
            toolchain.IsAvailable,
            toolchain.CaptureToolPath,
            toolchain.CaptureToolVersion,
            toolchain.SupportingToolVersion,
            toolchain.Message);
    }

    public async Task<string> StartCaptureAsync(
        NativeCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await profiling.StartCaptureAsync(
            new NativeProfileCaptureRequest(
                request.Platform,
                request.ApplicationPath,
                request.AppId,
                request.DeviceId,
                request.Preset,
                request.Duration,
                request.Headless),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> StartProcessSampleAsync(
        ProcessSampleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await profiling.StartProcessSampleAsync(
            new ProcessSampleCaptureRequest(
                request.ProcessId,
                request.Duration,
                request.IntervalMilliseconds),
            cancellationToken).ConfigureAwait(false);
    }

    public NativeCaptureSnapshot? GetCapture(string captureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        return profiling.TryGetCapture(captureId.Trim(), out var snapshot) && snapshot is not null
            ? ToPublic(snapshot)
            : null;
    }

    public IReadOnlyList<NativeCaptureManifest> ListCaptures()
        => profiling.ListCaptures().Select(ToPublic).ToArray();

    public NativeCaptureManifest? GetManifest(string captureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        return profiling.CaptureStore.TryLoadManifest(captureId.Trim(), out var manifest)
               && manifest is not null
            ? ToPublic(manifest)
            : null;
    }

    public string GetCaptureDirectoryPath(string captureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        return profiling.CaptureStore.GetCapturePath(captureId.Trim());
    }

    public string? GetProcessSampleArtifactPath(string captureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        var normalizedCaptureId = captureId.Trim();
        return profiling.TryGetCapture(normalizedCaptureId, out var snapshot)
               && snapshot is not null
               && string.Equals(
                   snapshot.Manifest.Platform,
                   NativeProfilePlatforms.Process,
                   StringComparison.Ordinal)
               && profiling.TryResolveArtifactPath(
                   normalizedCaptureId,
                   ProcessSampleCaptureAdapter.ArtifactKind,
                   out var artifactPath)
            ? artifactPath
            : null;
    }

    public NativeProfileArtifactLocation? GetArtifact(
        string captureId,
        string? artifactKind = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        var artifact = profiling.Inspection.GetArtifact(captureId.Trim(), artifactKind);
        return artifact is null ? null : ToPublic(artifact);
    }

    public async Task<NativeProfileCaptureInspection> InspectCaptureAsync(
        string captureId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        var inspection = await profiling.Inspection.InspectAsync(
            captureId.Trim(),
            cancellationToken).ConfigureAwait(false);
        return ToPublic(inspection);
    }

    public InstrumentsTocInspection InspectInstrumentsToc(string captureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        return ToPublic(profiling.Inspection.InspectInstrumentsToc(captureId.Trim()));
    }

    public async Task<NativeProfileInspectionToolchain> GetPerfettoInspectionToolchainAsync(
        CancellationToken cancellationToken = default)
        => ToPublic(await profiling.Inspection.GetPerfettoToolchainAsync(cancellationToken)
            .ConfigureAwait(false));

    public async Task<PerfettoQueryInspection> QueryPerfettoAsync(
        string captureId,
        string sql,
        int maximumRows = 1_000,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        var inspection = await profiling.Inspection.QueryPerfettoAsync(
            captureId.Trim(),
            sql,
            maximumRows,
            cancellationToken).ConfigureAwait(false);
        return ToPublic(inspection);
    }

    public bool CancelCapture(string captureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        return profiling.CancelCapture(captureId.Trim());
    }

    public void Dispose()
    {
        profiling.Dispose();
    }

    internal void ConfigureDeviceService(IDeviceService devices)
        => profiling.ConfigureDeviceService(devices);

    private static NativeCaptureSnapshot ToPublic(NativeProfileCaptureSnapshot snapshot)
        => new(
            snapshot.CaptureId,
            NormalizeEnum(snapshot.State),
            snapshot.Phase,
            snapshot.CreatedUtc,
            snapshot.StartedUtc,
            snapshot.CompletedUtc,
            snapshot.FailureMessage,
            ToPublic(snapshot.Manifest),
            snapshot.RecentLogLines);

    private static NativeCaptureManifest ToPublic(NativeProfileCaptureManifest manifest)
        => new(
            manifest.Schema,
            manifest.CaptureId,
            manifest.Platform,
            manifest.Engine,
            manifest.Preset,
            manifest.AppId,
            manifest.ApplicationPath,
            manifest.DeviceId,
            manifest.RequestedDurationSeconds,
            NormalizeEnum(manifest.State),
            manifest.CreatedUtc,
            manifest.StartedUtc,
            manifest.CompletedUtc,
            manifest.StopReason,
            manifest.FailureMessage,
            manifest.CaptureToolPath,
            manifest.CaptureToolVersion,
            manifest.SupportingToolVersion,
            manifest.Artifacts.Select(static artifact => new NativeCaptureArtifact(
                artifact.Kind,
                artifact.RelativePath,
                artifact.Length,
                artifact.Sha256,
                artifact.IsDirectory,
                artifact.Authoritative)).ToArray(),
            manifest.Warnings,
            manifest.ProcessId,
            manifest.ProcessName,
            manifest.SampleIntervalMilliseconds);

    private static NativeProfileArtifactLocation ToPublic(
        Runtime.NativeProfiling.NativeProfileArtifactLocation artifact)
        => new(
            artifact.CaptureId,
            artifact.Platform,
            artifact.Path,
            ToPublic(artifact.Artifact));

    private static NativeCaptureArtifact ToPublic(NativeProfileArtifact artifact)
        => new(
            artifact.Kind,
            artifact.RelativePath,
            artifact.Length,
            artifact.Sha256,
            artifact.IsDirectory,
            artifact.Authoritative);

    private static NativeProfileCaptureInspection ToPublic(
        Runtime.NativeProfiling.NativeProfileCaptureInspection inspection)
        => new(
            inspection.AnalyzerVersion,
            inspection.CaptureId,
            inspection.Platform,
            inspection.Engine,
            inspection.Preset,
            inspection.State,
            inspection.RequestedDurationSeconds,
            inspection.CaptureElapsedSeconds,
            inspection.CaptureDirectoryPath,
            inspection.AuthoritativeArtifact is null
                ? null
                : ToPublic(inspection.AuthoritativeArtifact),
            inspection.Artifacts.Select(ToPublic).ToArray(),
            inspection.Instruments is null ? null : ToPublic(inspection.Instruments),
            inspection.Perfetto is null ? null : ToPublic(inspection.Perfetto),
            inspection.Warnings);

    private static InstrumentsTocInspection ToPublic(
        Runtime.NativeProfiling.InstrumentsTocInspection inspection)
        => new(
            inspection.Path,
            inspection.RootElement,
            inspection.RunCount,
            inspection.TableCount,
            inspection.Schemas
                .Select(static schema => new InstrumentsSchemaInspection(
                    schema.Schema,
                    schema.Occurrences))
                .ToArray(),
            inspection.Processes
                .Select(static process => new InstrumentsProcessInspection(
                    process.ProcessId,
                    process.Name,
                    process.Path))
                .ToArray());

    private static NativeProfileInspectionToolchain ToPublic(
        Runtime.NativeProfiling.NativeProfileInspectionToolchain toolchain)
        => new(
            toolchain.IsAvailable,
            toolchain.ToolPath,
            toolchain.Version,
            toolchain.Message);

    private static PerfettoCaptureInspection ToPublic(
        Runtime.NativeProfiling.PerfettoCaptureInspection inspection)
        => new(
            inspection.ConfigPath,
            inspection.ConfiguredDurationMilliseconds,
            inspection.DataSources,
            ToPublic(inspection.Toolchain),
            inspection.TraceOverview is null ? null : ToPublic(inspection.TraceOverview),
            inspection.Warnings);

    private static PerfettoQueryInspection ToPublic(
        Runtime.NativeProfiling.PerfettoQueryInspection inspection)
        => new(
            inspection.CaptureId,
            ToPublic(inspection.Toolchain),
            inspection.Sql,
            inspection.Columns,
            inspection.Rows,
            inspection.WasTruncated,
            inspection.RowCount);

    private static string NormalizeEnum<T>(T value) where T : struct, Enum
    {
        var text = value.ToString();
        return text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
    }
}
