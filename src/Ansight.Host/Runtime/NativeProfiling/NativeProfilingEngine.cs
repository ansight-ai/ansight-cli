using System.Diagnostics;
using System.Text.RegularExpressions;
using Ansight.Host.Workspaces;

namespace Ansight.Host.Runtime.NativeProfiling;

internal sealed partial class NativeProfilingEngine : IDisposable
{
    internal const string CaptureLogArtifactKind = "capture-log";
    internal const string PartialTraceArtifactKind = "partial-native-trace";
    private readonly Lock jobsGate = new();
    private readonly ProductAnalytics analytics;
    private readonly Dictionary<string, CaptureJob> jobs = new(StringComparer.Ordinal);
    private readonly NativeProfileCaptureStore captureStore;
    private readonly NativeProfileInspectionService inspection;
    private readonly IReadOnlyList<INativeProfileCaptureAdapter> adapters;
    private readonly IProcessSampleCaptureAdapter processSampleAdapter;
    private IDeviceService? deviceService;
    private bool disposed;

    public NativeProfilingEngine(
        IApplicationPaths applicationPaths,
        INativeProfilingProcessRunner? processRunner = null,
        IDeviceService? deviceService = null,
        IReadOnlyList<INativeProfileCaptureAdapter>? adapters = null,
        IProcessSampleCaptureAdapter? processSampleAdapter = null)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        var runner = processRunner ?? new NativeProfilingProcessRunner();
        captureStore = new NativeProfileCaptureStore(applicationPaths);
        analytics = ProductAnalytics.For(applicationPaths);
        inspection = new NativeProfileInspectionService(captureStore, runner);
        this.deviceService = deviceService;
        this.adapters = adapters ??
        [
            new InstrumentsProfileCaptureAdapter(runner),
            new PerfettoProfileCaptureAdapter(runner)
        ];
        this.processSampleAdapter = processSampleAdapter ?? new ProcessSampleCaptureAdapter(runner);
    }

    public NativeProfileCaptureStore CaptureStore => captureStore;

    public NativeProfileInspectionService Inspection => inspection;

    public void ConfigureDeviceService(IDeviceService devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ObjectDisposedException.ThrowIf(disposed, this);
        deviceService = devices;
    }

    public Task<NativeProfileToolchain> GetToolchainAsync(
        string platform,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var normalizedPlatform = NormalizePlatform(platform);
        var adapter = adapters.FirstOrDefault(candidate => string.Equals(
                          candidate.Platform,
                          normalizedPlatform,
                          StringComparison.Ordinal))
                      ?? throw new InvalidOperationException(
                          $"No native profiling adapter is registered for '{normalizedPlatform}'.");
        return adapter.GetToolchainAsync(cancellationToken);
    }

    public Task<NativeProfileToolchain> GetProcessSampleToolchainAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return processSampleAdapter.GetToolchainAsync(cancellationToken);
    }

    public async Task<string> StartCaptureAsync(
        NativeProfileCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidateRequest(request);

        var normalizedPlatform = NormalizePlatform(request.Platform);
        var normalizedPreset = NormalizePreset(request.Preset);
        var applicationPath = Path.GetFullPath(request.ApplicationPath.Trim());
        var adapter = ResolveAdapter(normalizedPlatform, normalizedPreset);
        var captureId = Guid.NewGuid().ToString("N");
        var manifest = new NativeProfileCaptureManifest
        {
            Schema = NativeProfileCaptureManifest.CurrentSchema,
            CaptureId = captureId,
            Platform = normalizedPlatform,
            Engine = adapter.Engine,
            Preset = normalizedPreset,
            AppId = request.AppId.Trim(),
            ApplicationPath = applicationPath,
            DeviceId = request.DeviceId.Trim(),
            RequestedDurationSeconds = checked((int)Math.Ceiling(request.Duration.TotalSeconds)),
            State = NativeProfileCaptureState.Queued,
            CreatedUtc = DateTimeOffset.UtcNow
        };
        var job = new CaptureJob(manifest);
        lock (jobsGate)
        {
            var activeCapture = jobs.Values
                .Select(static candidate => candidate.CreateSnapshot())
                .FirstOrDefault(snapshot => !snapshot.IsTerminal
                                            && string.Equals(
                                                snapshot.Manifest.DeviceId,
                                                manifest.DeviceId,
                                                StringComparison.OrdinalIgnoreCase));
            if (activeCapture is not null)
            {
                throw new InvalidOperationException(
                    $"Device '{manifest.DeviceId}' already has active native capture "
                    + $"'{activeCapture.CaptureId}'. Cancel or wait for it before starting another.");
            }

            jobs.Add(captureId, job);
        }

        string capturePath;
        try
        {
            capturePath = captureStore.CreateCaptureDirectory(captureId);
            await captureStore.SaveManifestAsync(manifest, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(disposed, this);
        }
        catch
        {
            lock (jobsGate)
            {
                jobs.Remove(captureId);
            }

            job.Cancellation.Dispose();
            throw;
        }

        AppendLog(
            job,
            Path.Combine(capturePath, "diagnostics", "capture.log"),
            $"Queued {normalizedPlatform} {normalizedPreset} capture using {adapter.Engine}.");
        job.WorkTask = Task.Run(
            () => CaptureAsync(job, request with
            {
                Platform = normalizedPlatform,
                ApplicationPath = applicationPath,
                AppId = request.AppId.Trim(),
                DeviceId = request.DeviceId.Trim(),
                Preset = normalizedPreset
            }, adapter),
            CancellationToken.None);
        return captureId;
    }

    public async Task<string> StartProcessSampleAsync(
        ProcessSampleCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidateProcessSampleRequest(request);
        var target = ResolveProcessTarget(request.ProcessId);
        var captureId = Guid.NewGuid().ToString("N");
        var manifest = new NativeProfileCaptureManifest
        {
            Schema = NativeProfileCaptureManifest.ProcessSampleSchema,
            CaptureId = captureId,
            Platform = NativeProfilePlatforms.Process,
            Engine = processSampleAdapter.Engine,
            Preset = NativeProfilePresets.StackSample,
            AppId = target.Name,
            ApplicationPath = target.ExecutablePath ?? target.Name,
            DeviceId = Environment.MachineName,
            RequestedDurationSeconds = checked((int)Math.Ceiling(request.Duration.TotalSeconds)),
            ProcessId = request.ProcessId,
            ProcessName = target.Name,
            SampleIntervalMilliseconds = request.IntervalMilliseconds,
            State = NativeProfileCaptureState.Queued,
            CreatedUtc = DateTimeOffset.UtcNow
        };
        var job = new CaptureJob(manifest);
        lock (jobsGate)
        {
            var activeCapture = jobs.Values
                .Select(static candidate => candidate.CreateSnapshot())
                .FirstOrDefault(snapshot => !snapshot.IsTerminal
                                            && snapshot.Manifest.ProcessId == request.ProcessId);
            if (activeCapture is not null)
            {
                throw new InvalidOperationException(
                    $"Process {request.ProcessId} already has active stack sample "
                    + $"'{activeCapture.CaptureId}'. Cancel or wait for it before starting another.");
            }

            jobs.Add(captureId, job);
        }

        string capturePath;
        try
        {
            capturePath = captureStore.CreateCaptureDirectory(captureId);
            await captureStore.SaveManifestAsync(manifest, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(disposed, this);
        }
        catch
        {
            lock (jobsGate)
            {
                jobs.Remove(captureId);
            }

            job.Cancellation.Dispose();
            throw;
        }

        AppendLog(
            job,
            Path.Combine(capturePath, "diagnostics", "capture.log"),
            $"Queued stack sample for process {request.ProcessId} ({target.Name}) using "
            + $"{processSampleAdapter.Engine}.");
        job.WorkTask = Task.Run(
            () => CaptureProcessSampleAsync(job, request),
            CancellationToken.None);
        return captureId;
    }

    public bool TryGetCapture(string captureId, out NativeProfileCaptureSnapshot? snapshot)
    {
        CaptureJob? job;
        lock (jobsGate)
        {
            jobs.TryGetValue(captureId, out job);
        }

        if (job is not null)
        {
            snapshot = job.CreateSnapshot();
            return true;
        }

        if (captureStore.TryLoadManifest(captureId, out var manifest) && manifest is not null)
        {
            snapshot = new NativeProfileCaptureSnapshot(
                manifest.CaptureId,
                manifest.State,
                BuildPhase(manifest),
                manifest.CreatedUtc,
                manifest.StartedUtc,
                manifest.CompletedUtc,
                manifest.FailureMessage,
                manifest,
                ReadRecentLogLines(captureStore.GetCapturePath(captureId)));
            return true;
        }

        snapshot = null;
        return false;
    }

    public IReadOnlyList<NativeProfileCaptureManifest> ListCaptures()
        => captureStore.ListManifests();

    public bool TryResolveArtifactPath(
        string captureId,
        string artifactKind,
        out string? artifactPath)
    {
        artifactPath = null;
        return TryGetCapture(captureId, out var snapshot)
               && snapshot is not null
               && captureStore.TryResolveArtifactPath(
                   snapshot.Manifest,
                   artifactKind,
                   out artifactPath);
    }

    public bool CancelCapture(string captureId)
    {
        CaptureJob? job;
        lock (jobsGate)
        {
            jobs.TryGetValue(captureId, out job);
        }

        if (job is null || job.CreateSnapshot().IsTerminal)
        {
            return false;
        }

        job.Cancellation.Cancel();
        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        CaptureJob[] activeJobs;
        lock (jobsGate)
        {
            activeJobs = jobs.Values.ToArray();
        }

        foreach (var job in activeJobs)
        {
            job.Cancellation.Cancel();
            job.Cancellation.Dispose();
        }
    }

    private async Task CaptureAsync(
        CaptureJob job,
        NativeProfileCaptureRequest request,
        INativeProfileCaptureAdapter adapter)
    {
        var cancellationToken = job.Cancellation.Token;
        var capturePath = captureStore.GetCapturePath(job.CaptureId);
        var logPath = Path.Combine(capturePath, "diagnostics", "capture.log");
        try
        {
            AppendLog(job, logPath, "Inspecting the supplied application artifact and native toolchain.");
            await UpdateManifestAsync(job, manifest => manifest with
            {
                State = NativeProfileCaptureState.InspectingArtifact,
                StartedUtc = DateTimeOffset.UtcNow
            }, cancellationToken).ConfigureAwait(false);

            var devices = deviceService
                          ?? throw new InvalidOperationException(
                              "The host device service is not configured for native profiling.");
            using var package = WorkspaceTestApplicationPackage.Open(request.ApplicationPath);
            var device = await ResolveDeviceAsync(request, package, devices, cancellationToken)
                .ConfigureAwait(false);
            var toolchain = await adapter.GetToolchainAsync(cancellationToken).ConfigureAwait(false);
            if (!toolchain.IsAvailable)
            {
                throw new InvalidOperationException(toolchain.Message);
            }

            if (string.IsNullOrWhiteSpace(toolchain.CaptureToolPath))
            {
                throw new InvalidOperationException(
                    $"The {adapter.Engine} toolchain did not report its capture executable path.");
            }

            await UpdateManifestAsync(job, manifest => manifest with
            {
                DeviceId = device.Identifier,
                CaptureToolPath = toolchain.CaptureToolPath,
                CaptureToolVersion = toolchain.CaptureToolVersion,
                SupportingToolVersion = toolchain.SupportingToolVersion,
                State = NativeProfileCaptureState.PreparingDevice
            }, cancellationToken).ConfigureAwait(false);
            await PrepareDeviceAsync(
                request,
                package,
                device,
                devices,
                job,
                logPath,
                cancellationToken).ConfigureAwait(false);

            await UpdateManifestAsync(job, manifest => manifest with
            {
                State = NativeProfileCaptureState.Capturing
            }, cancellationToken).ConfigureAwait(false);
            var adapterResult = await adapter.CaptureAsync(
                new NativeProfileAdapterContext(
                    job.CaptureId,
                    capturePath,
                    toolchain.CaptureToolPath,
                    package.InstallPath,
                    request.AppId,
                    device.Identifier,
                    request.Preset,
                    request.Duration,
                    devices,
                    line => AppendLog(job, logPath, line)),
                cancellationToken).ConfigureAwait(false);

            await UpdateManifestAsync(job, manifest => manifest with
            {
                State = NativeProfileCaptureState.DerivingArtifacts
            }, cancellationToken).ConfigureAwait(false);
            AppendLog(job, logPath, "Native capture completed. Hashing and validating artifacts.");
            var artifacts = new List<NativeProfileArtifact>();
            foreach (var producedArtifact in adapterResult.Artifacts)
            {
                artifacts.Add(await captureStore.DescribeArtifactAsync(
                    job.CaptureId,
                    producedArtifact,
                    cancellationToken).ConfigureAwait(false));
            }

            AppendLog(job, logPath, "Capture completed successfully.");
            artifacts.Add(await captureStore.DescribeArtifactAsync(
                job.CaptureId,
                new NativeProfileProducedArtifact(
                    CaptureLogArtifactKind,
                    logPath,
                    IsDirectory: false,
                    Authoritative: false),
                cancellationToken).ConfigureAwait(false));
            await UpdateManifestAsync(job, manifest => manifest with
            {
                State = NativeProfileCaptureState.Completed,
                CompletedUtc = DateTimeOffset.UtcNow,
                StopReason = "capture-tool-completed",
                Artifacts = artifacts,
                Warnings = adapterResult.Warnings
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryAppendLog(job, logPath, "Capture cancellation requested. Retaining diagnostic artifacts.");
            var artifacts = await DescribeDiagnosticArtifactsAsync(job.CaptureId).ConfigureAwait(false);
            await UpdateManifestIgnoringCancellationAsync(job, manifest => manifest with
            {
                State = NativeProfileCaptureState.Cancelled,
                CompletedUtc = DateTimeOffset.UtcNow,
                StopReason = "cancelled",
                Artifacts = artifacts
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            TryAppendLog(job, logPath, $"Capture failed: {exception.Message}");
            var artifacts = await DescribeDiagnosticArtifactsAsync(job.CaptureId).ConfigureAwait(false);
            await UpdateManifestIgnoringCancellationAsync(job, manifest => manifest with
            {
                State = NativeProfileCaptureState.Failed,
                CompletedUtc = DateTimeOffset.UtcNow,
                StopReason = "failed",
                FailureMessage = exception.Message,
                Artifacts = artifacts
            }).ConfigureAwait(false);
        }
    }

    private async Task CaptureProcessSampleAsync(
        CaptureJob job,
        ProcessSampleCaptureRequest request)
    {
        var cancellationToken = job.Cancellation.Token;
        var capturePath = captureStore.GetCapturePath(job.CaptureId);
        var logPath = Path.Combine(capturePath, "diagnostics", "capture.log");
        try
        {
            AppendLog(job, logPath, $"Inspecting process {request.ProcessId} and the sample toolchain.");
            await UpdateManifestAsync(job, manifest => manifest with
            {
                State = NativeProfileCaptureState.InspectingProcess,
                StartedUtc = DateTimeOffset.UtcNow
            }, cancellationToken).ConfigureAwait(false);
            var target = ResolveProcessTarget(request.ProcessId);
            var toolchain = await processSampleAdapter.GetToolchainAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!toolchain.IsAvailable || string.IsNullOrWhiteSpace(toolchain.CaptureToolPath))
            {
                throw new InvalidOperationException(toolchain.Message);
            }

            await UpdateManifestAsync(job, manifest => manifest with
            {
                ApplicationPath = target.ExecutablePath ?? target.Name,
                ProcessName = target.Name,
                AppId = target.Name,
                CaptureToolPath = toolchain.CaptureToolPath,
                CaptureToolVersion = toolchain.CaptureToolVersion,
                SupportingToolVersion = toolchain.SupportingToolVersion,
                State = NativeProfileCaptureState.Capturing
            }, cancellationToken).ConfigureAwait(false);
            var adapterResult = await processSampleAdapter.CaptureAsync(
                new ProcessSampleAdapterContext(
                    capturePath,
                    request.ProcessId,
                    request.Duration,
                    request.IntervalMilliseconds,
                    line => AppendLog(job, logPath, line)),
                cancellationToken).ConfigureAwait(false);

            await UpdateManifestAsync(job, manifest => manifest with
            {
                State = NativeProfileCaptureState.DerivingArtifacts
            }, cancellationToken).ConfigureAwait(false);
            AppendLog(job, logPath, "Stack sampling completed. Hashing the sampled call graph.");
            var artifacts = new List<NativeProfileArtifact>();
            foreach (var producedArtifact in adapterResult.Artifacts)
            {
                artifacts.Add(await captureStore.DescribeArtifactAsync(
                    job.CaptureId,
                    producedArtifact,
                    cancellationToken).ConfigureAwait(false));
            }

            AppendLog(job, logPath, "Process stack sample completed successfully.");
            artifacts.Add(await captureStore.DescribeArtifactAsync(
                job.CaptureId,
                new NativeProfileProducedArtifact(
                    CaptureLogArtifactKind,
                    logPath,
                    IsDirectory: false,
                    Authoritative: false),
                cancellationToken).ConfigureAwait(false));
            await UpdateManifestAsync(job, manifest => manifest with
            {
                State = NativeProfileCaptureState.Completed,
                CompletedUtc = DateTimeOffset.UtcNow,
                StopReason = "capture-tool-completed",
                Artifacts = artifacts,
                Warnings = adapterResult.Warnings
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryAppendLog(job, logPath, "Stack-sample cancellation requested. Retaining diagnostic artifacts.");
            var artifacts = await DescribeDiagnosticArtifactsAsync(job.CaptureId).ConfigureAwait(false);
            await UpdateManifestIgnoringCancellationAsync(job, manifest => manifest with
            {
                State = NativeProfileCaptureState.Cancelled,
                CompletedUtc = DateTimeOffset.UtcNow,
                StopReason = "cancelled",
                Artifacts = artifacts
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            TryAppendLog(job, logPath, $"Stack sample failed: {exception.Message}");
            var artifacts = await DescribeDiagnosticArtifactsAsync(job.CaptureId).ConfigureAwait(false);
            await UpdateManifestIgnoringCancellationAsync(job, manifest => manifest with
            {
                State = NativeProfileCaptureState.Failed,
                CompletedUtc = DateTimeOffset.UtcNow,
                StopReason = "failed",
                FailureMessage = exception.Message,
                Artifacts = artifacts
            }).ConfigureAwait(false);
        }
    }

    private static async Task<DeviceDescriptor> ResolveDeviceAsync(
        NativeProfileCaptureRequest request,
        WorkspaceTestApplicationPackage package,
        IDeviceService devices,
        CancellationToken cancellationToken)
    {
        var inventory = await devices.ListAsync(cancellationToken).ConfigureAwait(false);
        var device = inventory.Devices.FirstOrDefault(candidate => candidate.IsAvailable
                                                                  && string.Equals(
                                                                      candidate.Identifier,
                                                                      request.DeviceId,
                                                                      StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            throw new InvalidOperationException(
                $"Device '{request.DeviceId}' was not found or is unavailable.");
        }

        if (!string.Equals(device.Platform, request.Platform, StringComparison.Ordinal)
            || !string.Equals(package.Platform, request.Platform, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Artifact, requested platform, and device must all target '{request.Platform}'.");
        }

        if (!DeviceKinds.Matches(package.RequiredDeviceKind, device.Kind))
        {
            throw new InvalidOperationException(
                $"Artifact '{package.SourcePath}' requires a '{package.RequiredDeviceKind}' target, "
                + $"but device '{device.Identifier}' is '{device.Kind}'.");
        }

        return device;
    }

    private static async Task PrepareDeviceAsync(
        NativeProfileCaptureRequest request,
        WorkspaceTestApplicationPackage package,
        DeviceDescriptor device,
        IDeviceService devices,
        CaptureJob job,
        string logPath,
        CancellationToken cancellationToken)
    {
        if (!device.IsBooted)
        {
            AppendLog(job, logPath, $"Starting device '{device.Identifier}'.");
            EnsureDeviceOperationSucceeded(await devices.StartAsync(
                device.Platform,
                device.Identifier,
                new DeviceStartOptions(Headless: request.Headless || DeviceLaunchContext.Headless),
                cancellationToken).ConfigureAwait(false));
        }
        else if (device.IsVirtual && !request.Headless && !DeviceLaunchContext.Headless)
        {
            EnsureDeviceOperationSucceeded(await devices.ShowWindowAsync(
                device.Platform,
                device.Identifier,
                cancellationToken).ConfigureAwait(false));
        }

        AppendLog(job, logPath, $"Installing artifact '{package.SourcePath}'.");
        EnsureDeviceOperationSucceeded(await devices.InstallApplicationAsync(
            device.Platform,
            device.Identifier,
            package.InstallPath,
            cancellationToken).ConfigureAwait(false));

        AppendLog(job, logPath, $"Ensuring '{request.AppId}' is stopped before capture.");
        EnsureDeviceOperationSucceeded(await devices.TerminateApplicationAsync(
            device.Platform,
            device.Identifier,
            request.AppId,
            cancellationToken).ConfigureAwait(false));
    }

    private async Task<IReadOnlyList<NativeProfileArtifact>> DescribeDiagnosticArtifactsAsync(
        string captureId)
    {
        var capturePath = captureStore.GetCapturePath(captureId);
        var candidates = new[]
        {
            new NativeProfileProducedArtifact(
                CaptureLogArtifactKind,
                Path.Combine(capturePath, "diagnostics", "capture.log"),
                IsDirectory: false,
                Authoritative: false),
            new NativeProfileProducedArtifact(
                PerfettoProfileCaptureAdapter.ConfigArtifactKind,
                Path.Combine(capturePath, "diagnostics", "perfetto-config.pbtxt"),
                IsDirectory: false,
                Authoritative: false),
            new NativeProfileProducedArtifact(
                PartialTraceArtifactKind,
                Path.Combine(capturePath, "raw", "capture.trace"),
                IsDirectory: true,
                Authoritative: false),
            new NativeProfileProducedArtifact(
                PartialTraceArtifactKind,
                Path.Combine(capturePath, "raw", "capture.perfetto-trace"),
                IsDirectory: false,
                Authoritative: false),
            new NativeProfileProducedArtifact(
                ProcessSampleCaptureAdapter.ArtifactKind,
                Path.Combine(capturePath, "raw", "sample.txt"),
                IsDirectory: false,
                Authoritative: false)
        };
        var artifacts = new List<NativeProfileArtifact>();
        foreach (var candidate in candidates)
        {
            var exists = candidate.IsDirectory ? Directory.Exists(candidate.Path) : File.Exists(candidate.Path);
            if (!exists)
            {
                continue;
            }

            try
            {
                artifacts.Add(await captureStore.DescribeArtifactAsync(
                    captureId,
                    candidate,
                    CancellationToken.None).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return artifacts;
    }

    private async Task UpdateManifestAsync(
        CaptureJob job,
        Func<NativeProfileCaptureManifest, NativeProfileCaptureManifest> update,
        CancellationToken cancellationToken)
    {
        var manifest = job.Update(update);
        await captureStore.SaveManifestAsync(manifest, cancellationToken).ConfigureAwait(false);
        ReportCaptureUsage(job, manifest);
    }

    private async Task UpdateManifestIgnoringCancellationAsync(
        CaptureJob job,
        Func<NativeProfileCaptureManifest, NativeProfileCaptureManifest> update)
    {
        var manifest = job.Update(update);
        await captureStore.SaveManifestAsync(manifest, CancellationToken.None).ConfigureAwait(false);
        ReportCaptureUsage(job, manifest);
    }

    private INativeProfileCaptureAdapter ResolveAdapter(string platform, string preset)
        => adapters.FirstOrDefault(candidate => string.Equals(
                                                   candidate.Platform,
                                                   platform,
                                                   StringComparison.Ordinal)
                                               && candidate.SupportsPreset(preset))
           ?? throw new ArgumentException(
               $"Native preset '{preset}' is not available for platform '{platform}'.",
               nameof(preset));

    private static void ValidateRequest(NativeProfileCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = NormalizePlatform(request.Platform);
        _ = NormalizePreset(request.Preset);
        if (string.IsNullOrWhiteSpace(request.ApplicationPath)
            || (!File.Exists(request.ApplicationPath) && !Directory.Exists(request.ApplicationPath)))
        {
            throw new FileNotFoundException(
                "An existing .app, .ipa, or .apk application artifact is required.",
                request.ApplicationPath);
        }

        if (string.IsNullOrWhiteSpace(request.AppId)
            || !SafeApplicationIdentifierPattern().IsMatch(request.AppId.Trim()))
        {
            throw new ArgumentException("A valid bundle or package identifier is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.DeviceId))
        {
            throw new ArgumentException("A device identifier is required.", nameof(request));
        }

        if (request.Duration < TimeSpan.FromSeconds(1) || request.Duration > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Native capture duration must be between 1 and 300 seconds.");
        }
    }

    private static void ValidateProcessSampleRequest(ProcessSampleCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "A positive process identifier is required.");
        }

        if (request.Duration < TimeSpan.FromSeconds(1) || request.Duration > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Process sample duration must be between 1 and 300 seconds.");
        }

        if (request.IntervalMilliseconds is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Process sample interval must be between 1 and 1000 milliseconds.");
        }
    }

    private static ProcessSampleTarget ResolveProcessTarget(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                throw new InvalidOperationException($"Process {processId} has already exited.");
            }

            var processName = process.ProcessName;
            string? executablePath = null;
            try
            {
                executablePath = process.MainModule?.FileName;
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
            catch (NotSupportedException)
            {
            }

            return new ProcessSampleTarget(processName, executablePath);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                $"Process {processId} was not found or has already exited.",
                exception);
        }
    }

    private static string NormalizePlatform(string platform)
        => platform?.Trim().ToLowerInvariant() switch
        {
            NativeProfilePlatforms.Ios => NativeProfilePlatforms.Ios,
            NativeProfilePlatforms.Android => NativeProfilePlatforms.Android,
            _ => throw new ArgumentException("Native profiling platform must be ios or android.", nameof(platform))
        };

    private static string NormalizePreset(string preset)
        => preset?.Trim().ToLowerInvariant() switch
        {
            NativeProfilePresets.Launch => NativeProfilePresets.Launch,
            NativeProfilePresets.Cpu => NativeProfilePresets.Cpu,
            NativeProfilePresets.System => NativeProfilePresets.System,
            NativeProfilePresets.Memory => NativeProfilePresets.Memory,
            NativeProfilePresets.Leaks => NativeProfilePresets.Leaks,
            NativeProfilePresets.NativeHeap => NativeProfilePresets.NativeHeap,
            NativeProfilePresets.ManagedHeap => NativeProfilePresets.ManagedHeap,
            _ => throw new ArgumentException(
                "Native profiling preset must be launch, cpu, system, memory, leaks, native-heap, or managed-heap.",
                nameof(preset))
        };

    private static void EnsureDeviceOperationSucceeded(DeviceOperationResult result)
    {
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(result.Message);
        }
    }

    private static void AppendLog(CaptureJob job, string logPath, string message)
    {
        var line = $"[{DateTimeOffset.UtcNow:O}] {message}";
        job.AppendLog(logPath, line);
    }

    private static void TryAppendLog(CaptureJob job, string logPath, string message)
    {
        try
        {
            AppendLog(job, logPath, message);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static IReadOnlyList<string> ReadRecentLogLines(string capturePath)
    {
        var logPath = Path.Combine(capturePath, "diagnostics", "capture.log");
        if (!File.Exists(logPath))
        {
            return [];
        }

        try
        {
            return File.ReadLines(logPath).TakeLast(200).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string BuildPhase(NativeProfileCaptureManifest manifest)
        => manifest.State switch
        {
            NativeProfileCaptureState.Queued => "Queued",
            NativeProfileCaptureState.InspectingArtifact => "Inspecting application artifact and toolchain",
            NativeProfileCaptureState.InspectingProcess => "Inspecting local process and sample toolchain",
            NativeProfileCaptureState.PreparingDevice => "Installing application artifact",
            NativeProfileCaptureState.Capturing when manifest.ProcessId.HasValue => "Sampling process stacks",
            NativeProfileCaptureState.Capturing => "Capturing native profile",
            NativeProfileCaptureState.DerivingArtifacts when manifest.ProcessId.HasValue
                => "Hashing sampled call graph",
            NativeProfileCaptureState.DerivingArtifacts => "Validating and hashing artifacts",
            NativeProfileCaptureState.Completed => "Completed",
            NativeProfileCaptureState.Failed => "Failed",
            NativeProfileCaptureState.Cancelled => "Cancelled",
            _ => manifest.State.ToString()
        };

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]+$")]
    private static partial Regex SafeApplicationIdentifierPattern();

    private void ReportCaptureUsage(CaptureJob job, NativeProfileCaptureManifest manifest)
    {
        if (manifest.CompletedUtc is not { } completed || Interlocked.Exchange(ref job.UsageReported, 1) != 0) return;
        analytics.RecordUsage((manifest.Schema == NativeProfileCaptureManifest.ProcessSampleSchema ? "profile_sample" : manifest.Platform == "ios" ? "profile_ios" : "profile_android"),
            outcome: manifest.State == NativeProfileCaptureState.Completed ? "succeeded" : manifest.State == NativeProfileCaptureState.Cancelled ? "cancelled" : "failed",
            durationSeconds: Math.Max(0, (completed - (manifest.StartedUtc ?? manifest.CreatedUtc)).TotalSeconds));
    }

    private sealed class CaptureJob
    {
        public int UsageReported;
        private const int MaximumRecentLogLineCount = 200;
        private readonly Lock gate = new();
        private readonly Lock logGate = new();
        private readonly Queue<string> recentLogLines = new(MaximumRecentLogLineCount);
        private NativeProfileCaptureManifest manifest;

        public CaptureJob(NativeProfileCaptureManifest manifest)
        {
            this.manifest = manifest;
        }

        public string CaptureId => manifest.CaptureId;

        public CancellationTokenSource Cancellation { get; } = new();

        public Task? WorkTask { get; set; }

        public void AppendLog(string logPath, string line)
        {
            lock (logGate)
            {
                File.AppendAllText(logPath, line + Environment.NewLine);
                if (recentLogLines.Count == MaximumRecentLogLineCount)
                {
                    recentLogLines.Dequeue();
                }

                recentLogLines.Enqueue(line);
            }
        }

        public NativeProfileCaptureManifest Update(
            Func<NativeProfileCaptureManifest, NativeProfileCaptureManifest> update)
        {
            lock (gate)
            {
                manifest = update(manifest);
                return manifest;
            }
        }

        public NativeProfileCaptureSnapshot CreateSnapshot()
        {
            lock (gate)
            {
                return new NativeProfileCaptureSnapshot(
                    manifest.CaptureId,
                    manifest.State,
                    BuildPhase(manifest),
                    manifest.CreatedUtc,
                    manifest.StartedUtc,
                    manifest.CompletedUtc,
                    manifest.FailureMessage,
                    manifest,
                    GetRecentLogLines());
            }
        }

        private IReadOnlyList<string> GetRecentLogLines()
        {
            lock (logGate)
            {
                return recentLogLines.ToArray();
            }
        }
    }

    private sealed record ProcessSampleTarget(string Name, string? ExecutablePath);
}
