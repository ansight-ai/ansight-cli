using System.ComponentModel.Composition;
using Ansight.Host.Runtime.DotNetProfiling;

namespace Ansight.Host.Profiling;

[Export(typeof(DotNetProfilingService))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class DotNetProfilingService : IDisposable
{
    private readonly DotNetProfilingEngine profiling;

    [ImportingConstructor]
    internal DotNetProfilingService(IApplicationPaths applicationPaths)
    {
        profiling = new DotNetProfilingEngine(
            applicationPaths ?? throw new ArgumentNullException(nameof(applicationPaths)));
    }

    internal DotNetProfilingEngine InternalService => profiling;

    public async Task<DotNetProfilingToolchain> GetToolchainAsync(
        CancellationToken cancellationToken = default)
    {
        var toolchain = await profiling.GetToolchainAsync(cancellationToken).ConfigureAwait(false);
        return new DotNetProfilingToolchain(
            toolchain.IsAvailable,
            toolchain.IsAndroidEmulatorAvailable || toolchain.IsAndroidDeviceAvailable,
            toolchain.IsIosSimulatorAvailable,
            toolchain.IsIosDeviceAvailable,
            toolchain.DotNetTracePath,
            toolchain.DotNetDsRouterPath,
            toolchain.AdbPath,
            toolchain.XcrunPath,
            toolchain.SimCtlPath,
            toolchain.DotNetTraceVersion,
            toolchain.DotNetDsRouterVersion,
            toolchain.AdbVersion,
            toolchain.XcodeVersion);
    }

    public async Task<string> StartCaptureAsync(
        DotNetCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await profiling.StartStartupCaptureAsync(
            new DotNetStartupCaptureRequest(
                request.ApplicationPath,
                request.AppId,
                request.DeviceId,
                request.Duration,
                request.SymbolsPath,
                request.Headless),
            cancellationToken).ConfigureAwait(false);
    }

    public DotNetCaptureSnapshot? GetCapture(string captureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        return profiling.TryGetCapture(captureId.Trim(), out var snapshot) && snapshot is not null
            ? ToPublic(snapshot)
            : null;
    }

    public IReadOnlyList<DotNetCaptureManifest> ListCaptures()
        => profiling.ListCaptures().Select(ToPublic).ToArray();

    public DotNetCaptureManifest? GetManifest(string captureId)
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

    public DotNetCaptureArtifactLocation? GetSpeedScopeArtifact(string captureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        var normalizedCaptureId = captureId.Trim();
        if (!profiling.CaptureStore.TryLoadManifest(normalizedCaptureId, out var manifest)
            || manifest is null
            || !profiling.CaptureStore.TryResolveArtifactPath(
                manifest,
                DotNetProfilingEngine.SpeedScopeArtifactKind,
                out var filePath)
            || filePath is null)
        {
            return null;
        }

        var artifact = manifest.Artifacts.First(candidate => string.Equals(
            candidate.Kind,
            DotNetProfilingEngine.SpeedScopeArtifactKind,
            StringComparison.Ordinal));
        return new DotNetCaptureArtifactLocation(
            normalizedCaptureId,
            filePath,
            ToPublic(artifact),
            Authoritative: false);
    }

    public bool CancelCapture(string captureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        return profiling.CancelCapture(captureId.Trim());
    }

    public async Task<DotNetCaptureManifest> ImportTraceAsync(
        string tracePath,
        string? appId,
        string? applicationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tracePath);
        var manifest = await profiling.ImportTraceAsync(
            Path.GetFullPath(tracePath.Trim()),
            appId,
            applicationPath,
            cancellationToken).ConfigureAwait(false);
        return ToPublic(manifest);
    }

    public DotNetTraceAnalysisResult AnalyzeCapture(DotNetTraceAnalysisRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CaptureId);
        var selection = request.Selection ?? new DotNetTraceSelection(null, null, null, null);
        ValidateSelection(selection);
        ValidateLimit(request.HotspotLimit, 1, 1_000, nameof(request.HotspotLimit));
        ValidateLimit(request.CallTreeDepth, 1, 256, nameof(request.CallTreeDepth));
        ValidateLimit(request.CallTreeChildLimit, 1, 1_000, nameof(request.CallTreeChildLimit));

        var analysisResult = profiling.Analysis.Analyze(
            request.CaptureId.Trim(),
            new TraceSelectionWindow(
                selection.StartMilliseconds,
                selection.EndMilliseconds,
                selection.ProcessId,
                selection.ThreadId),
            request.HotspotLimit,
            request.CallTreeDepth,
            request.CallTreeChildLimit);
        return ToPublic(analysisResult);
    }

    public void Dispose()
    {
        profiling.Dispose();
    }

    internal void ConfigureDeviceService(IDeviceService devices)
        => profiling.ConfigureDeviceService(devices);

    private static DotNetCaptureSnapshot ToPublic(DotNetTraceCaptureSnapshot snapshot)
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

    private static DotNetTraceAnalysisResult ToPublic(ParsedTraceAnalysisResult result)
    {
        var traceAnalysis = result.Analysis;
        return new DotNetTraceAnalysisResult(
            result.Manifest.CaptureId,
            DotNetTraceAnalysisService.AnalyzerVersion,
            new DotNetTraceSelection(
                result.Selection.StartMilliseconds,
                result.Selection.EndMilliseconds,
                result.Selection.ProcessId,
                result.Selection.ThreadId),
            new DotNetTraceEvidence(
                result.NetTraceArtifact.Kind,
                result.NetTraceArtifact.RelativePath,
                result.NetTraceArtifact.Length,
                result.NetTraceArtifact.Sha256),
            new DotNetTraceAnalysis(
                new DotNetTraceOverview(
                    traceAnalysis.Overview.StartMilliseconds,
                    traceAnalysis.Overview.EndMilliseconds,
                    traceAnalysis.Overview.DurationMilliseconds,
                    traceAnalysis.Overview.EventCount,
                    traceAnalysis.Overview.CpuSampleCount,
                    traceAnalysis.Overview.ProcessCount,
                    traceAnalysis.Overview.ThreadCount,
                    traceAnalysis.Overview.GcCount,
                    traceAnalysis.Overview.TotalGcPauseMilliseconds,
                    traceAnalysis.Overview.JittedMethodCount,
                    traceAnalysis.Overview.ExceptionCount,
                    traceAnalysis.Overview.SymbolicatedSamplePercentage),
                traceAnalysis.CpuHotspots.Select(static hotspot => new DotNetCpuHotspot(
                    hotspot.Method,
                    hotspot.Module,
                    hotspot.ExclusiveSamples,
                    hotspot.InclusiveSamples,
                    hotspot.ExclusivePercentage,
                    hotspot.InclusivePercentage)).ToArray(),
                ToPublic(traceAnalysis.CallTree),
                traceAnalysis.GcPauses.Select(static pause => new DotNetGcPause(
                    pause.Number,
                    pause.Generation,
                    pause.Reason,
                    pause.Type,
                    pause.StartMilliseconds,
                    pause.EndMilliseconds,
                    pause.DurationMilliseconds,
                    pause.ProcessId,
                    pause.ThreadId)).ToArray(),
                traceAnalysis.JittedMethods.Select(static method => new DotNetJitMethod(
                    method.Method,
                    method.IlSize,
                    method.TimestampMilliseconds,
                    method.ProcessId,
                    method.ThreadId)).ToArray(),
                traceAnalysis.Exceptions.Select(static exception => new DotNetExceptionGroup(
                    exception.Type,
                    exception.Message,
                    exception.Count,
                    exception.FirstTimestampMilliseconds,
                    exception.LastTimestampMilliseconds)).ToArray(),
                traceAnalysis.Threads.Select(static thread => new DotNetThreadActivity(
                    thread.ProcessId,
                    thread.ThreadId,
                    thread.ProcessName,
                    thread.ThreadName,
                    thread.CpuSampleCount,
                    thread.CpuSamplePercentage)).ToArray(),
                traceAnalysis.StartupMilestones.Select(static milestone => new DotNetStartupMilestone(
                    ResolveStartupMilestoneKind(milestone),
                    milestone.Provider,
                    milestone.EventName,
                    milestone.TimestampMilliseconds,
                    milestone.ProcessId,
                    milestone.ThreadId)).ToArray(),
                traceAnalysis.ProviderEventCounts,
                traceAnalysis.EventNameCounts,
                traceAnalysis.Warnings));
    }

    private static DotNetCallTreeNode ToPublic(ParsedCallTreeNode node)
        => new(
            node.Name,
            node.Module,
            node.SampleCount,
            node.Children.Select(ToPublic).ToArray());

    private static DotNetCaptureManifest ToPublic(DotNetTraceCaptureManifest manifest)
        => new(
            manifest.Schema,
            manifest.CaptureId,
            manifest.AppId,
            manifest.ApplicationPath,
            manifest.Platform,
            manifest.ArtifactKind,
            manifest.LaunchAdapter,
            manifest.DeviceId,
            manifest.CapturePreset,
            manifest.RequestedDurationSeconds,
            NormalizeEnum(manifest.State),
            manifest.CreatedUtc,
            manifest.StartedUtc,
            manifest.CompletedUtc,
            manifest.StopReason,
            manifest.FailureMessage,
            manifest.DotNetTraceVersion,
            manifest.DotNetDsRouterVersion,
            manifest.AdbVersion,
            manifest.XcodeVersion,
            manifest.Artifacts.Select(ToPublic).ToArray(),
            manifest.Warnings);

    private static DotNetCaptureArtifact ToPublic(DotNetTraceArtifact artifact)
        => new(
            artifact.Kind,
            artifact.RelativePath,
            artifact.Length,
            artifact.Sha256);

    private static void ValidateSelection(DotNetTraceSelection selection)
    {
        if (selection.StartMilliseconds is { } start
            && (!double.IsFinite(start) || start < 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(selection),
                "The trace start offset must be a finite, non-negative number.");
        }

        if (selection.EndMilliseconds is { } end
            && (!double.IsFinite(end) || end < 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(selection),
                "The trace end offset must be a finite, non-negative number.");
        }

        if (selection.StartMilliseconds is { } rangeStart
            && selection.EndMilliseconds is { } rangeEnd
            && rangeEnd < rangeStart)
        {
            throw new ArgumentException(
                "The trace end offset must be greater than or equal to the start offset.",
                nameof(selection));
        }

        if (selection.ProcessId is < 0 || selection.ThreadId is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(selection),
                "Process and thread identifiers cannot be negative.");
        }
    }

    private static void ValidateLimit(int value, int minimum, int maximum, string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"The value must be between {minimum} and {maximum}.");
        }
    }

    private static string ResolveStartupMilestoneKind(ParsedStartupMilestone milestone)
    {
        if (string.Equals(
                milestone.EventName,
                DotNetStartupMarker.EventName,
                StringComparison.Ordinal))
        {
            return "application-ready";
        }

        return string.Equals(
            milestone.EventName,
            DotNetStartupMarker.ManagedModuleInitializedEventName,
            StringComparison.Ordinal)
            ? "managed-module-initialized"
            : "runtime-event";
    }

    private static string NormalizeEnum<T>(T value) where T : struct, Enum
    {
        var text = value.ToString();
        var characters = text.SelectMany((character, index) =>
            index > 0 && char.IsUpper(character)
                ? new[] { '-', char.ToLowerInvariant(character) }
                : new[] { char.ToLowerInvariant(character) });
        return new string(characters.ToArray());
    }
}
