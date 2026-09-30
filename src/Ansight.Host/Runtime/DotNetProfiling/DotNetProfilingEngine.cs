using System.Text.Json;
using Ansight.Host.Workspaces;

namespace Ansight.Host.Runtime.DotNetProfiling;

internal sealed class DotNetProfilingEngine : IDisposable
{
    internal const string NetTraceArtifactKind = "dotnet-nettrace";
    internal const string SpeedScopeArtifactKind = "dotnet-speedscope";
    internal const string CaptureLogArtifactKind = "capture-log";
    internal const string ApplicationMetadataArtifactKind = "application-metadata";
    internal const string PortablePdbArtifactKind = "portable-pdb";

    private readonly Lock jobsGate = new();
    private readonly ProductAnalytics analytics;
    private readonly Dictionary<string, CaptureJob> jobs = new(StringComparer.Ordinal);
    private readonly DotNetTraceCaptureStore captureStore;
    private readonly DotNetTraceToolLocator toolLocator;
    private readonly IDotNetTraceProcessRunner processRunner;
    private IDeviceService? deviceService;
    private bool disposed;

    public DotNetProfilingEngine(
        IApplicationPaths applicationPaths,
        IDotNetTraceProcessRunner? processRunner = null,
        IDeviceService? deviceService = null)
    {
        this.processRunner = processRunner ?? new DotNetTraceProcessRunner();
        this.deviceService = deviceService;
        captureStore = new DotNetTraceCaptureStore(applicationPaths);
        analytics = ProductAnalytics.For(applicationPaths);
        Analysis = new DotNetTraceAnalysisService(captureStore);
        toolLocator = new DotNetTraceToolLocator(this.processRunner);
    }

    public DotNetTraceCaptureStore CaptureStore => captureStore;

    public DotNetTraceAnalysisService Analysis { get; }

    public void ConfigureDeviceService(IDeviceService devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ObjectDisposedException.ThrowIf(disposed, this);
        deviceService = devices;
    }

    public Task<DotNetTraceToolchain> GetToolchainAsync(CancellationToken cancellationToken = default)
        => toolLocator.ResolveAsync(cancellationToken);

    public string StartStartupCapture(DotNetStartupCaptureRequest request)
        => StartStartupCaptureAsync(request).GetAwaiter().GetResult();

    public async Task<string> StartStartupCaptureAsync(
        DotNetStartupCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidateRequest(request);

        var applicationPath = Path.GetFullPath(request.ApplicationPath);
        var captureId = Guid.NewGuid().ToString("N");
        var manifest = new DotNetTraceCaptureManifest
        {
            Schema = DotNetTraceCaptureManifest.CurrentSchema,
            CaptureId = captureId,
            AppId = request.AppId.Trim(),
            ApplicationPath = applicationPath,
            Platform = ResolveArtifactPlatform(applicationPath),
            ArtifactKind = Path.GetExtension(applicationPath).TrimStart('.').ToLowerInvariant(),
            DeviceId = request.DeviceId.Trim(),
            CapturePreset = "startup-explain-v2",
            RequestedDurationSeconds = checked((int)Math.Ceiling(request.Duration.TotalSeconds)),
            State = DotNetTraceCaptureState.Queued,
            CreatedUtc = DateTimeOffset.UtcNow
        };
        var job = new CaptureJob(manifest);
        var capturePath = captureStore.CreateCaptureDirectory(captureId);
        await captureStore.SaveManifestAsync(manifest, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (jobsGate)
        {
            jobs.Add(captureId, job);
        }

        AppendLog(
            job,
            Path.Combine(capturePath, "diagnostics", "capture.log"),
            $"Capture queued for artifact '{Path.GetFileName(applicationPath)}' on device '{request.DeviceId.Trim()}'.");
        job.WorkTask = Task.Run(() => CaptureAsync(job, request), CancellationToken.None);
        return captureId;
    }

    public bool TryGetCapture(string captureId, out DotNetTraceCaptureSnapshot? snapshot)
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
            snapshot = new DotNetTraceCaptureSnapshot(
                manifest.CaptureId,
                manifest.State,
                BuildPhase(manifest.State),
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

    public IReadOnlyList<DotNetTraceCaptureManifest> ListCaptures()
        => captureStore.ListManifests();

    public bool CancelCapture(string captureId)
    {
        CaptureJob? job;
        lock (jobsGate)
        {
            jobs.TryGetValue(captureId, out job);
        }

        if (job is null || job.CreateSnapshot().State is DotNetTraceCaptureState.Completed
            or DotNetTraceCaptureState.Failed
            or DotNetTraceCaptureState.Cancelled)
        {
            return false;
        }

        job.Cancellation.Cancel();
        return true;
    }

    public async Task<DotNetTraceCaptureManifest> ImportTraceAsync(
        string tracePath,
        string? appId,
        string? applicationPath,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (string.IsNullOrWhiteSpace(tracePath) || !File.Exists(tracePath))
        {
            throw new FileNotFoundException("The .nettrace file could not be found.", tracePath);
        }

        if (!string.Equals(Path.GetExtension(tracePath), ".nettrace", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Imported traces must have a .nettrace extension.", nameof(tracePath));
        }

        var captureId = Guid.NewGuid().ToString("N");
        var capturePath = captureStore.CreateCaptureDirectory(captureId);
        var rawTracePath = Path.Combine(capturePath, "raw", "runtime.nettrace");
        File.Copy(tracePath, rawTracePath, overwrite: false);
        var artifacts = new List<DotNetTraceArtifact>
        {
            await captureStore.DescribeArtifactAsync(captureId, NetTraceArtifactKind, rawTracePath, cancellationToken)
        };
        var warnings = new List<string>();
        var toolchain = await toolLocator.ResolveAsync(cancellationToken);
        if (toolchain.DotNetTracePath is not null)
        {
            var speedScopePath = await ConvertToSpeedScopeAsync(
                toolchain.DotNetTracePath,
                capturePath,
                rawTracePath,
                cancellationToken);
            if (speedScopePath is not null)
            {
                artifacts.Add(await captureStore.DescribeArtifactAsync(
                    captureId,
                    SpeedScopeArtifactKind,
                    speedScopePath,
                    cancellationToken));
            }
            else
            {
                warnings.Add("The imported NetTrace could not be converted to SpeedScope.");
            }
        }
        else
        {
            warnings.Add("dotnet-trace was not found, so no SpeedScope derivative was created.");
        }

        var sourceApplicationPath = NormalizeOptional(applicationPath);
        var now = DateTimeOffset.UtcNow;
        var manifest = new DotNetTraceCaptureManifest
        {
            Schema = DotNetTraceCaptureManifest.CurrentSchema,
            CaptureId = captureId,
            AppId = NormalizeOptional(appId) ?? string.Empty,
            ApplicationPath = sourceApplicationPath is null
                ? string.Empty
                : Path.GetFullPath(sourceApplicationPath),
            Platform = sourceApplicationPath is null ? null : TryResolveArtifactPlatform(sourceApplicationPath),
            ArtifactKind = sourceApplicationPath is null
                ? null
                : Path.GetExtension(sourceApplicationPath).TrimStart('.').ToLowerInvariant(),
            CapturePreset = "imported-nettrace-v2",
            RequestedDurationSeconds = 0,
            State = DotNetTraceCaptureState.Completed,
            CreatedUtc = now,
            StartedUtc = now,
            CompletedUtc = now,
            StopReason = "imported",
            DotNetTraceVersion = toolchain.DotNetTraceVersion,
            DotNetDsRouterVersion = toolchain.DotNetDsRouterVersion,
            AdbVersion = toolchain.AdbVersion,
            XcodeVersion = toolchain.XcodeVersion,
            Artifacts = artifacts,
            Warnings = warnings
        };
        await captureStore.SaveManifestAsync(manifest, cancellationToken);
        return manifest;
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

    private async Task CaptureAsync(CaptureJob job, DotNetStartupCaptureRequest request)
    {
        var cancellationToken = job.Cancellation.Token;
        var capturePath = captureStore.GetCapturePath(job.CaptureId);
        var logPath = Path.Combine(capturePath, "diagnostics", "capture.log");
        var warnings = new List<string>();
        try
        {
            AppendLog(job, logPath, "Inspecting the supplied application artifact and capture toolchain.");
            await UpdateManifestAsync(job, manifest => manifest with
            {
                State = DotNetTraceCaptureState.InspectingArtifact,
                StartedUtc = DateTimeOffset.UtcNow
            }, cancellationToken);

            var devices = deviceService
                          ?? throw new InvalidOperationException(
                              "The host device service is not configured for artifact capture.");
            using var package = WorkspaceTestApplicationPackage.Open(request.ApplicationPath);
            var target = await ResolveLaunchTargetAsync(
                request,
                package,
                devices,
                cancellationToken);
            var applicationManifest = DotNetProfilingArtifactInspector.TryRead(package);
            if (applicationManifest is null)
            {
                const string missingManifestWarning =
                    "The artifact does not contain ansight/dotnet-profiling.json. "
                    + "Ansight will attempt capture using the manually configured diagnostic endpoint.";
                warnings.Add(missingManifestWarning);
                AppendLog(job, logPath, missingManifestWarning);
            }
            else
            {
                DotNetProfilingArtifactInspector.EnsureCompatible(applicationManifest, target.Adapter);
                AppendLog(
                    job,
                    logPath,
                    $"Verified Ansight profiling manifest for '{applicationManifest.Target}'.");
            }

            var toolchain = await toolLocator.ResolveAsync(cancellationToken);
            EnsureToolchainAvailable(target.Adapter, toolchain);

            await UpdateManifestAsync(job, manifest => manifest with
            {
                Platform = package.Platform,
                LaunchAdapter = target.Adapter.ToString(),
                DeviceId = target.Device.Identifier,
                DotNetTraceVersion = toolchain.DotNetTraceVersion,
                DotNetDsRouterVersion = toolchain.DotNetDsRouterVersion,
                AdbVersion = toolchain.AdbVersion,
                XcodeVersion = toolchain.XcodeVersion,
                State = DotNetTraceCaptureState.PreparingDevice
            }, cancellationToken);

            await PrepareDeviceAsync(
                request,
                package,
                target,
                devices,
                job,
                logPath,
                cancellationToken);
            var metadataPath = await WriteApplicationMetadataAsync(
                capturePath,
                request,
                package,
                target,
                applicationManifest,
                cancellationToken);
            var symbolArtifacts = CopySymbols(request.SymbolsPath, capturePath);

            await UpdateManifestAsync(job, manifest => manifest with
            {
                State = DotNetTraceCaptureState.Capturing
            }, cancellationToken);
            AppendLog(job, logPath, $"Starting {request.Duration:g} EventPipe capture.");

            var rawTracePath = Path.Combine(capturePath, "raw", "runtime.nettrace");
            var traceResult = await CaptureRoutedApplicationAsync(
                job,
                request,
                target,
                devices,
                toolchain,
                rawTracePath,
                logPath,
                cancellationToken);
            if (!traceResult.IsSuccess)
            {
                throw new InvalidOperationException(
                    "dotnet-trace could not connect to the supplied artifact's diagnostic endpoint: "
                    + BuildFailureMessage(traceResult)
                    + $". Build the artifact with EnableDiagnostics=true and DiagnosticConfiguration="
                    + $"'{BuildExpectedDiagnosticConfiguration(target.Adapter)}'.");
            }

            if (!File.Exists(rawTracePath))
            {
                throw new FileNotFoundException(
                    "dotnet-trace completed without producing runtime.nettrace.",
                    rawTracePath);
            }

            var stoppedOnStartupMarker = TraceContainsApplicationReadyMarker(rawTracePath);
            await UpdateManifestAsync(job, manifest => manifest with
            {
                State = DotNetTraceCaptureState.DerivingArtifacts
            }, cancellationToken);
            AppendLog(job, logPath, "Trace collection finished. Deriving and hashing artifacts.");

            var artifacts = new List<DotNetTraceArtifact>
            {
                await captureStore.DescribeArtifactAsync(
                    job.CaptureId,
                    NetTraceArtifactKind,
                    rawTracePath,
                    cancellationToken),
                await captureStore.DescribeArtifactAsync(
                    job.CaptureId,
                    ApplicationMetadataArtifactKind,
                    metadataPath,
                    cancellationToken)
            };
            foreach (var symbolArtifact in symbolArtifacts)
            {
                artifacts.Add(await captureStore.DescribeArtifactAsync(
                    job.CaptureId,
                    symbolArtifact.Kind,
                    symbolArtifact.Path,
                    cancellationToken));
            }

            var speedScopePath = FindSpeedScopePath(capturePath, rawTracePath);
            if (speedScopePath is not null)
            {
                speedScopePath = MoveSpeedScopeToDerivedFolder(capturePath, speedScopePath);
            }

            speedScopePath ??= await ConvertToSpeedScopeAsync(
                toolchain.DotNetTracePath!,
                capturePath,
                rawTracePath,
                cancellationToken);
            if (speedScopePath is not null)
            {
                artifacts.Add(await captureStore.DescribeArtifactAsync(
                    job.CaptureId,
                    SpeedScopeArtifactKind,
                    speedScopePath,
                    cancellationToken));
            }

            AppendLog(job, logPath, "Capture completed successfully.");
            if (File.Exists(logPath))
            {
                artifacts.Add(await captureStore.DescribeArtifactAsync(
                    job.CaptureId,
                    CaptureLogArtifactKind,
                    logPath,
                    cancellationToken));
            }

            if (speedScopePath is null)
            {
                warnings.Add("The NetTrace capture succeeded, but its SpeedScope derivative could not be created.");
            }

            if (!stoppedOnStartupMarker)
            {
                warnings.Add(
                    "The artifact did not emit the optional Ansight application-ready marker; "
                    + "startup analysis uses runtime and trace-boundary milestones.");
            }

            await UpdateManifestAsync(job, manifest => manifest with
            {
                State = DotNetTraceCaptureState.Completed,
                CompletedUtc = DateTimeOffset.UtcNow,
                StopReason = stoppedOnStartupMarker ? "application-ready-marker" : "duration",
                Artifacts = artifacts,
                Warnings = warnings
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryAppendLog(job, logPath, "Capture cancellation requested. Retaining diagnostic artifacts.");
            var artifacts = await DescribeDiagnosticArtifactsAsync(job.CaptureId);
            await UpdateManifestIgnoringCancellationAsync(job, manifest => manifest with
            {
                State = DotNetTraceCaptureState.Cancelled,
                CompletedUtc = DateTimeOffset.UtcNow,
                StopReason = "cancelled",
                Artifacts = artifacts
            });
        }
        catch (Exception ex)
        {
            TryAppendLog(job, logPath, $"Capture failed: {ex.Message}");
            var artifacts = await DescribeDiagnosticArtifactsAsync(job.CaptureId);
            await UpdateManifestIgnoringCancellationAsync(job, manifest => manifest with
            {
                State = DotNetTraceCaptureState.Failed,
                CompletedUtc = DateTimeOffset.UtcNow,
                StopReason = "failed",
                FailureMessage = ex.Message,
                Artifacts = artifacts
            });
        }
    }

    private static async Task<ResolvedLaunchTarget> ResolveLaunchTargetAsync(
        DotNetStartupCaptureRequest request,
        WorkspaceTestApplicationPackage package,
        IDeviceService devices,
        CancellationToken cancellationToken)
    {
        var inventory = await devices.ListAsync(cancellationToken).ConfigureAwait(false);
        var device = inventory.Devices.FirstOrDefault(candidate =>
            candidate.IsAvailable
            && string.Equals(candidate.Identifier, request.DeviceId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            throw new InvalidOperationException(
                $"Device '{request.DeviceId.Trim()}' was not found or is unavailable.");
        }

        if (!string.Equals(device.Platform, package.Platform, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Artifact '{package.SourcePath}' targets '{package.Platform}', "
                + $"but device '{device.Identifier}' is '{device.Platform}'.");
        }

        if (!DeviceKinds.Matches(package.RequiredDeviceKind, device.Kind))
        {
            throw new InvalidOperationException(
                $"Artifact '{package.SourcePath}' requires a '{package.RequiredDeviceKind}' target, "
                + $"but device '{device.Identifier}' is '{device.Kind}'.");
        }

        var adapter = device.Platform switch
        {
            DevicePlatforms.Android when device.IsPhysical => DotNetCaptureLaunchAdapter.AndroidDevice,
            DevicePlatforms.Android => DotNetCaptureLaunchAdapter.AndroidEmulator,
            DevicePlatforms.Ios when device.IsPhysical => DotNetCaptureLaunchAdapter.IosDevice,
            DevicePlatforms.Ios => DotNetCaptureLaunchAdapter.IosSimulator,
            _ => throw new InvalidOperationException(
                $".NET artifact capture does not support platform '{device.Platform}'.")
        };
        return new ResolvedLaunchTarget(adapter, device);
    }

    private static void EnsureToolchainAvailable(
        DotNetCaptureLaunchAdapter adapter,
        DotNetTraceToolchain toolchain)
    {
        var available = adapter switch
        {
            DotNetCaptureLaunchAdapter.AndroidEmulator => toolchain.IsAndroidEmulatorAvailable,
            DotNetCaptureLaunchAdapter.AndroidDevice => toolchain.IsAndroidDeviceAvailable,
            DotNetCaptureLaunchAdapter.IosSimulator => toolchain.IsIosSimulatorAvailable,
            DotNetCaptureLaunchAdapter.IosDevice => toolchain.IsIosDeviceAvailable,
            _ => false
        };
        if (available)
        {
            return;
        }

        throw new InvalidOperationException(adapter switch
        {
            DotNetCaptureLaunchAdapter.AndroidEmulator or DotNetCaptureLaunchAdapter.AndroidDevice =>
                "Android artifact capture requires dotnet-trace, dotnet-dsrouter, and ADB.",
            DotNetCaptureLaunchAdapter.IosSimulator =>
                "iOS Simulator artifact capture requires macOS, dotnet-trace, dotnet-dsrouter, Xcode, and SimCtl.",
            DotNetCaptureLaunchAdapter.IosDevice =>
                "iOS device artifact capture requires macOS, dotnet-trace, dotnet-dsrouter, and Xcode.",
            _ => "The .NET artifact capture toolchain is unavailable."
        });
    }

    private static async Task PrepareDeviceAsync(
        DotNetStartupCaptureRequest request,
        WorkspaceTestApplicationPackage package,
        ResolvedLaunchTarget target,
        IDeviceService devices,
        CaptureJob job,
        string logPath,
        CancellationToken cancellationToken)
    {
        if (!target.Device.IsBooted)
        {
            AppendLog(job, logPath, $"Starting device '{target.Device.Identifier}'.");
            var startResult = await devices.StartAsync(
                target.Device.Platform,
                target.Device.Identifier,
                new DeviceStartOptions(Headless: request.Headless || DeviceLaunchContext.Headless),
                cancellationToken).ConfigureAwait(false);
            EnsureDeviceOperationSucceeded(startResult);
        }
        else if (target.Device.IsVirtual && !request.Headless && !DeviceLaunchContext.Headless)
        {
            EnsureDeviceOperationSucceeded(await devices.ShowWindowAsync(
                target.Device.Platform,
                target.Device.Identifier,
                cancellationToken).ConfigureAwait(false));
        }

        AppendLog(job, logPath, $"Installing artifact '{package.SourcePath}'.");
        var installResult = await devices.InstallApplicationAsync(
            target.Device.Platform,
            target.Device.Identifier,
            package.InstallPath,
            cancellationToken).ConfigureAwait(false);
        EnsureDeviceOperationSucceeded(installResult);

        AppendLog(job, logPath, $"Ensuring '{request.AppId.Trim()}' is stopped before startup capture.");
        var terminateResult = await devices.TerminateApplicationAsync(
            target.Device.Platform,
            target.Device.Identifier,
            request.AppId.Trim(),
            cancellationToken).ConfigureAwait(false);
        EnsureDeviceOperationSucceeded(terminateResult);
    }

    private async Task<DotNetTraceProcessResult> CaptureRoutedApplicationAsync(
        CaptureJob job,
        DotNetStartupCaptureRequest request,
        ResolvedLaunchTarget target,
        IDeviceService devices,
        DotNetTraceToolchain toolchain,
        string rawTracePath,
        string logPath,
        CancellationToken cancellationToken)
    {
        var dsRouterArguments = BuildDsRouterArguments(target.Adapter);
        AppendLog(job, logPath, $"dotnet-dsrouter {string.Join(' ', dsRouterArguments)}");
        await using var dsRouter = DotNetTraceOwnedProcess.Start(
            new DotNetTraceProcessRequest(
                toolchain.DotNetDsRouterPath!,
                dsRouterArguments,
                Path.GetDirectoryName(request.ApplicationPath) ?? Environment.CurrentDirectory,
                EnvironmentVariables: BuildRouterEnvironment(target)),
            line => AppendLog(job, logPath, line));
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        if (dsRouter.Completion.IsCompleted)
        {
            throw new InvalidOperationException(
                "dotnet-dsrouter exited before the target runtime and trace collector could connect. "
                + "Port 9000 may already be in use.");
        }

        var traceArguments = BuildTraceCollectionArguments(request, rawTracePath);
        traceArguments.Add("--process-id");
        traceArguments.Add(dsRouter.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendLog(job, logPath, $"dotnet-trace {string.Join(' ', traceArguments.Select(QuoteForLog))}");
        var traceTask = processRunner.RunAsync(
            new DotNetTraceProcessRequest(
                toolchain.DotNetTracePath!,
                traceArguments,
                Path.GetDirectoryName(request.ApplicationPath) ?? Environment.CurrentDirectory,
                request.Duration + TimeSpan.FromMinutes(3)),
            line => AppendLog(job, logPath, line),
            cancellationToken);

        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        if (traceTask.IsCompleted)
        {
            return await traceTask.ConfigureAwait(false);
        }

        AppendLog(job, logPath, $"Launching '{request.AppId.Trim()}' on '{target.Device.Identifier}'.");
        var launchResult = await devices.LaunchApplicationAsync(
            target.Device.Platform,
            target.Device.Identifier,
            request.AppId.Trim(),
            cancellationToken).ConfigureAwait(false);
        EnsureDeviceOperationSucceeded(launchResult);
        return await traceTask.ConfigureAwait(false);
    }

    private static List<string> BuildTraceCollectionArguments(
        DotNetStartupCaptureRequest request,
        string rawTracePath)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling(request.Duration.TotalSeconds)));
        var providers = "Microsoft-Windows-DotNETRuntime:0x100003801D:5"
                        + $",{DotNetStartupMarker.ProviderName}:0xFFFFFFFFFFFFFFFF:5";
        return
        [
            "collect",
            "--profile",
            "dotnet-common,dotnet-sampled-thread-time",
            "--providers",
            providers,
            "--format",
            "Speedscope",
            "--output",
            rawTracePath,
            "--duration",
            duration.ToString(@"dd\:hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture),
            "--stopping-event-provider-name",
            DotNetStartupMarker.ProviderName,
            "--stopping-event-event-name",
            DotNetStartupMarker.EventName
        ];
    }

    private static IReadOnlyList<string> BuildDsRouterArguments(DotNetCaptureLaunchAdapter adapter)
        =>
        [
            adapter switch
            {
                DotNetCaptureLaunchAdapter.AndroidEmulator => "android-emu",
                DotNetCaptureLaunchAdapter.AndroidDevice => "android",
                DotNetCaptureLaunchAdapter.IosSimulator => "ios-sim",
                DotNetCaptureLaunchAdapter.IosDevice => "ios",
                _ => throw new ArgumentOutOfRangeException(nameof(adapter), adapter, null)
            },
            "--runtime-timeout",
            "90",
            "--verbose",
            "info"
        ];

    private static IReadOnlyDictionary<string, string?>? BuildRouterEnvironment(
        ResolvedLaunchTarget target)
        => target.Adapter is DotNetCaptureLaunchAdapter.AndroidEmulator
            or DotNetCaptureLaunchAdapter.AndroidDevice
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ANDROID_SERIAL"] = target.Device.Identifier
            }
            : null;

    private static string BuildExpectedDiagnosticConfiguration(DotNetCaptureLaunchAdapter adapter)
        => adapter switch
        {
            DotNetCaptureLaunchAdapter.AndroidEmulator => "10.0.2.2:9000,suspend,connect",
            DotNetCaptureLaunchAdapter.AndroidDevice => "127.0.0.1:9000,suspend,connect",
            DotNetCaptureLaunchAdapter.IosSimulator or DotNetCaptureLaunchAdapter.IosDevice =>
                "127.0.0.1:9000,suspend,listen",
            _ => throw new ArgumentOutOfRangeException(nameof(adapter), adapter, null)
        };

    private static void EnsureDeviceOperationSucceeded(DeviceOperationResult result)
    {
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(result.Message);
        }
    }

    private static async Task<string> WriteApplicationMetadataAsync(
        string capturePath,
        DotNetStartupCaptureRequest request,
        WorkspaceTestApplicationPackage package,
        ResolvedLaunchTarget target,
        DotNetProfilingApplicationManifest? profilingManifest,
        CancellationToken cancellationToken)
    {
        var metadataPath = Path.Combine(capturePath, "diagnostics", "application.json");
        var metadata = new ApplicationCaptureMetadata(
            package.SourcePath,
            package.Platform,
            Path.GetExtension(package.SourcePath).TrimStart('.').ToLowerInvariant(),
            request.AppId.Trim(),
            target.Device.Identifier,
            target.Device.Kind,
            target.Adapter.ToString(),
            BuildExpectedDiagnosticConfiguration(target.Adapter),
            profilingManifest);
        await File.WriteAllTextAsync(
            metadataPath,
            JsonSerializer.Serialize(metadata, JsonUtil.Pretty),
            cancellationToken);
        return metadataPath;
    }

    private static IReadOnlyList<StagedArtifact> CopySymbols(string? symbolsPath, string capturePath)
    {
        if (string.IsNullOrWhiteSpace(symbolsPath))
        {
            return [];
        }

        var fullSymbolsPath = Path.GetFullPath(symbolsPath.Trim());
        var sourceFiles = File.Exists(fullSymbolsPath)
            ? [fullSymbolsPath]
            : Directory.EnumerateFiles(fullSymbolsPath, "*.pdb", SearchOption.AllDirectories).ToArray();
        var destinationRoot = Path.Combine(capturePath, "symbols");
        var artifacts = new List<StagedArtifact>();
        foreach (var sourceFile in sourceFiles)
        {
            var relativePath = File.Exists(fullSymbolsPath)
                ? Path.GetFileName(sourceFile)
                : Path.GetRelativePath(fullSymbolsPath, sourceFile);
            var destinationPath = Path.Combine(destinationRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourceFile, destinationPath, overwrite: true);
            artifacts.Add(new StagedArtifact(PortablePdbArtifactKind, destinationPath));
        }

        return artifacts;
    }

    private async Task<string?> ConvertToSpeedScopeAsync(
        string dotNetTracePath,
        string capturePath,
        string rawTracePath,
        CancellationToken cancellationToken)
    {
        var outputBasePath = Path.Combine(capturePath, "derived", "runtime");
        var result = await processRunner.RunAsync(
            new DotNetTraceProcessRequest(
                dotNetTracePath,
                ["convert", rawTracePath, "--format", "Speedscope", "--output", outputBasePath],
                capturePath,
                TimeSpan.FromMinutes(5)),
            outputReceived: null,
            cancellationToken);
        if (!result.IsSuccess)
        {
            return null;
        }

        return Directory.EnumerateFiles(Path.Combine(capturePath, "derived"), "*.speedscope.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private async Task UpdateManifestAsync(
        CaptureJob job,
        Func<DotNetTraceCaptureManifest, DotNetTraceCaptureManifest> update,
        CancellationToken cancellationToken)
    {
        var manifest = job.Update(update);
        await captureStore.SaveManifestAsync(manifest, cancellationToken);
        ReportCaptureUsage(job, manifest);
    }

    private async Task UpdateManifestIgnoringCancellationAsync(
        CaptureJob job,
        Func<DotNetTraceCaptureManifest, DotNetTraceCaptureManifest> update)
    {
        var manifest = job.Update(update);
        await captureStore.SaveManifestAsync(manifest, CancellationToken.None);
        ReportCaptureUsage(job, manifest);
    }

    private async Task<IReadOnlyList<DotNetTraceArtifact>> DescribeDiagnosticArtifactsAsync(string captureId)
    {
        var capturePath = captureStore.GetCapturePath(captureId);
        var candidates = new List<StagedArtifact>
        {
            new(CaptureLogArtifactKind, Path.Combine(capturePath, "diagnostics", "capture.log")),
            new(ApplicationMetadataArtifactKind, Path.Combine(capturePath, "diagnostics", "application.json"))
        };
        var symbolsPath = Path.Combine(capturePath, "symbols");
        if (Directory.Exists(symbolsPath))
        {
            candidates.AddRange(Directory.EnumerateFiles(symbolsPath, "*.pdb", SearchOption.AllDirectories)
                .Select(path => new StagedArtifact(PortablePdbArtifactKind, path)));
        }

        var artifacts = new List<DotNetTraceArtifact>();
        foreach (var candidate in candidates.Where(candidate => File.Exists(candidate.Path)))
        {
            try
            {
                artifacts.Add(await captureStore.DescribeArtifactAsync(
                    captureId,
                    candidate.Kind,
                    candidate.Path,
                    CancellationToken.None));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return artifacts;
    }

    private static void ValidateRequest(DotNetStartupCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ApplicationPath))
        {
            throw new ArgumentException("An application artifact path is required.", nameof(request));
        }

        var applicationPath = Path.GetFullPath(request.ApplicationPath);
        if (!File.Exists(applicationPath) && !Directory.Exists(applicationPath))
        {
            throw new FileNotFoundException("The application artifact could not be found.", applicationPath);
        }

        ResolveArtifactPlatform(applicationPath);
        if (string.IsNullOrWhiteSpace(request.AppId))
        {
            throw new ArgumentException("An application id is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.DeviceId))
        {
            throw new ArgumentException("A device id is required.", nameof(request));
        }

        if (request.Duration < TimeSpan.FromSeconds(1) || request.Duration > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Capture duration must be between 1 and 300 seconds.");
        }

        if (!string.IsNullOrWhiteSpace(request.SymbolsPath))
        {
            var symbolsPath = Path.GetFullPath(request.SymbolsPath.Trim());
            if (!File.Exists(symbolsPath) && !Directory.Exists(symbolsPath))
            {
                throw new FileNotFoundException("The symbols path could not be found.", symbolsPath);
            }

            if (File.Exists(symbolsPath)
                && !string.Equals(Path.GetExtension(symbolsPath), ".pdb", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("A symbols file must have a .pdb extension.", nameof(request));
            }
        }
    }

    private static string ResolveArtifactPlatform(string applicationPath)
        => TryResolveArtifactPlatform(applicationPath)
           ?? throw new ArgumentException(
               $"Unsupported application artifact '{applicationPath}'. Expected an .app, .apk, or .ipa.",
               nameof(applicationPath));

    private static string? TryResolveArtifactPlatform(string applicationPath)
        => Path.GetExtension(applicationPath).ToLowerInvariant() switch
        {
            ".apk" => DevicePlatforms.Android,
            ".app" or ".ipa" => DevicePlatforms.Ios,
            _ => null
        };

    private static string? FindSpeedScopePath(string capturePath, string rawTracePath)
    {
        var likelyPath = Path.Combine(
            Path.GetDirectoryName(rawTracePath) ?? capturePath,
            Path.GetFileNameWithoutExtension(rawTracePath) + ".speedscope.json");
        if (File.Exists(likelyPath))
        {
            return likelyPath;
        }

        return Directory.EnumerateFiles(capturePath, "*.speedscope.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static string MoveSpeedScopeToDerivedFolder(string capturePath, string speedScopePath)
    {
        var derivedPath = Path.Combine(capturePath, "derived", "runtime.speedscope.json");
        if (string.Equals(
                Path.GetFullPath(speedScopePath),
                Path.GetFullPath(derivedPath),
                StringComparison.Ordinal))
        {
            return speedScopePath;
        }

        File.Move(speedScopePath, derivedPath, overwrite: true);
        return derivedPath;
    }

    private static string BuildFailureMessage(DotNetTraceProcessResult result)
    {
        if (result.TimedOut)
        {
            return "the command timed out";
        }

        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        return string.IsNullOrWhiteSpace(detail) ? $"exit code {result.ExitCode}" : detail.Trim();
    }

    private static bool TraceContainsApplicationReadyMarker(string tracePath)
    {
        try
        {
            var markerObserved = false;
            using var source = new Microsoft.Diagnostics.Tracing.EventPipeEventSource(tracePath);
            source.Dynamic.All += traceEvent =>
            {
                if (string.Equals(
                        traceEvent.ProviderName,
                        DotNetStartupMarker.ProviderName,
                        StringComparison.Ordinal)
                    && string.Equals(
                        traceEvent.EventName,
                        DotNetStartupMarker.EventName,
                        StringComparison.Ordinal))
                {
                    markerObserved = true;
                }
            };
            source.Process();
            return markerObserved;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string QuoteForLog(string argument)
        => argument.Any(char.IsWhiteSpace) ? $"\"{argument.Replace("\"", "\\\"")}\"" : argument;

    private static void AppendLog(CaptureJob job, string logPath, string line)
    {
        job.AppendLog(logPath, $"[{DateTimeOffset.UtcNow:O}] {line}");
    }

    private static void TryAppendLog(CaptureJob job, string logPath, string line)
    {
        try
        {
            AppendLog(job, logPath, line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static IReadOnlyList<string> ReadRecentLogLines(string capturePath)
    {
        const int maximumLineCount = 200;
        var logPath = Path.Combine(capturePath, "diagnostics", "capture.log");
        if (!File.Exists(logPath))
        {
            return [];
        }

        try
        {
            using var stream = new FileStream(
                logPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new Queue<string>(maximumLineCount);
            while (reader.ReadLine() is { } line)
            {
                if (lines.Count == maximumLineCount)
                {
                    lines.Dequeue();
                }

                lines.Enqueue(line);
            }

            return lines.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string BuildPhase(DotNetTraceCaptureState state)
        => state switch
        {
            DotNetTraceCaptureState.Queued => "Queued",
            DotNetTraceCaptureState.InspectingArtifact => "Inspecting application artifact and toolchain",
            DotNetTraceCaptureState.PreparingDevice => "Installing application artifact",
            DotNetTraceCaptureState.Capturing => "Capturing EventPipe trace",
            DotNetTraceCaptureState.DerivingArtifacts => "Hashing and deriving artifacts",
            DotNetTraceCaptureState.Completed => "Completed",
            DotNetTraceCaptureState.Failed => "Failed",
            DotNetTraceCaptureState.Cancelled => "Cancelled",
            _ => state.ToString()
        };

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void ReportCaptureUsage(CaptureJob job, DotNetTraceCaptureManifest manifest)
    {
        if (manifest.CompletedUtc is not { } completed || Interlocked.Exchange(ref job.UsageReported, 1) != 0) return;
        analytics.RecordUsage("profile_dotnet",
            outcome: manifest.State == DotNetTraceCaptureState.Completed ? "succeeded" : manifest.State == DotNetTraceCaptureState.Cancelled ? "cancelled" : "failed",
            durationSeconds: Math.Max(0, (completed - (manifest.StartedUtc ?? manifest.CreatedUtc)).TotalSeconds));
    }

    private sealed class CaptureJob
    {
        public int UsageReported;
        private const int MaximumRecentLogLineCount = 200;
        private readonly Lock gate = new();
        private readonly Lock logGate = new();
        private readonly Queue<string> recentLogLines = new(MaximumRecentLogLineCount);
        private DotNetTraceCaptureManifest manifest;

        public CaptureJob(DotNetTraceCaptureManifest manifest)
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

        public DotNetTraceCaptureManifest Update(
            Func<DotNetTraceCaptureManifest, DotNetTraceCaptureManifest> update)
        {
            lock (gate)
            {
                manifest = update(manifest);
                return manifest;
            }
        }

        public DotNetTraceCaptureSnapshot CreateSnapshot()
        {
            lock (gate)
            {
                return new DotNetTraceCaptureSnapshot(
                    manifest.CaptureId,
                    manifest.State,
                    BuildPhase(manifest.State),
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

    private sealed record ResolvedLaunchTarget(
        DotNetCaptureLaunchAdapter Adapter,
        DeviceDescriptor Device);

    private sealed record StagedArtifact(string Kind, string Path);

    private sealed record ApplicationCaptureMetadata(
        string ApplicationPath,
        string Platform,
        string ArtifactKind,
        string AppId,
        string DeviceId,
        string DeviceKind,
        string LaunchAdapter,
        string RequiredDiagnosticConfiguration,
        DotNetProfilingApplicationManifest? ProfilingManifest);
}
