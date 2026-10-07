namespace Ansight.Host.Runtime.Screenshots;

using System.Text.Json;
using System.Diagnostics;
using Ansight.Adb;
using Ansight.Host.Apps;
using Ansight.Host.Runtime.DeviceExecution;
using Ansight.Pairing.Models;
using Ansight.SimCtl;
using Ansight.Infrastructure.Preferences;

[Export(typeof(IExternalSessionScreenshotCaptureManager))]
[PartCreationPolicy(CreationPolicy.Shared)]
internal sealed class ExternalSessionScreenshotCaptureManager : IExternalSessionScreenshotCaptureManager
{
    private static readonly Ansight.Infrastructure.Logging.ILogger log = Ansight.Infrastructure.Logging.Logger.Create();
    private static readonly TimeSpan captureTimeout = TimeSpan.FromSeconds(10);
    private const int maximumConsecutiveFailures = 3;

    private readonly IRuntimeState runtimeState;
    private readonly IUserPreferences userPreferences;
    private readonly Lock captureGate = new();
    private readonly Dictionary<string, CaptureHandle> capturesBySessionId = new(StringComparer.Ordinal);
    private Func<string, string, CancellationToken, Task<byte[]>>? physicalIosCapture;

    [ImportingConstructor]
    public ExternalSessionScreenshotCaptureManager(
        IRuntimeState runtimeState,
        IUserPreferences userPreferences)
    {
        this.runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
        this.userPreferences = userPreferences ?? throw new ArgumentNullException(nameof(userPreferences));
    }

    public event EventHandler<ExternalSessionScreenshotCaptureFailedEventArgs>? CaptureFailed;

    internal void ConfigurePhysicalIosCapture(Func<string, string, CancellationToken, Task<byte[]>> capture)
        => physicalIosCapture = capture ?? throw new ArgumentNullException(nameof(capture));

    public async Task<ExternalSessionScreenshotCapturePolicy> AttachAsync(
        string sessionId,
        DeviceAppProfile? profile,
        string? profileJson,
        ExternalSessionScreenshotCaptureRequest captureRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(captureRequest);

        await StopAsync(sessionId, "External screenshot target changed.").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(sessionId) || profile is null)
        {
            return ExternalSessionScreenshotCapturePolicy.App("The session did not provide a device profile.");
        }

        var osName = profile.Device?.OsName?.Trim();
        var isPhysicalDevice = profile.Device?.IsVirtual != true && profile.Device?.IsEmulator != true;
        var physicalIosDeviceSession = isPhysicalDevice
            && string.Equals(osName, "ios", StringComparison.OrdinalIgnoreCase)
            && runtimeState.IsDeviceSessionActive(sessionId);
        if (isPhysicalDevice && string.Equals(osName, "ios", StringComparison.OrdinalIgnoreCase)
            && !physicalIosDeviceSession)
            return ExternalSessionScreenshotCapturePolicy.App(
                "Physical iOS SDK sessions use app-managed screenshot capture.");
        if (isPhysicalDevice
            && !string.Equals(osName, "android", StringComparison.OrdinalIgnoreCase)
            && physicalIosCapture is null)
        {
            return ExternalSessionScreenshotCapturePolicy.App(
                "Physical iOS screenshot capture requires Appium/WebDriverAgent.");
        }

        try
        {
            using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCancellation.CancelAfter(captureTimeout);
            CaptureTarget? target;
            if (physicalIosDeviceSession)
            {
                var deviceId = ResolveNativeDeviceId(profileJson);
                var appId = profile.App?.AppId;
                var capture = physicalIosCapture;
                target = deviceId is null || string.IsNullOrWhiteSpace(appId) || capture is null
                    ? null : new CaptureTarget("appium-xcuitest", token => capture(deviceId, appId, token));
            }
            else
            {
                target = runtimeState.IsDeviceSessionActive(sessionId)
                    ? await ResolveDeviceRunTargetAsync(profile, profileJson, probeCancellation.Token).ConfigureAwait(false)
                    : await ResolveTargetAsync(profile, profileJson, probeCancellation.Token).ConfigureAwait(false);
            }
            if (target is null)
            {
                return ExternalSessionScreenshotCapturePolicy.App(
                    "Ansight could not uniquely match the running app to a host-controlled device.");
            }

            var probe = await target.CaptureAsync(probeCancellation.Token).ConfigureAwait(false);
            AddFrame(
                sessionId,
                probe,
                ExternalSessionScreenshotCaptureProfile.Standard(captureRequest));

            var cancellation = new CancellationTokenSource();
            var handle = new CaptureHandle(cancellation, target, captureRequest);
            lock (captureGate)
            {
                capturesBySessionId[sessionId] = handle;
                handle.Task = Task.Run(() => RunCapturePumpAsync(sessionId, handle));
            }

            log.Info($"external_session_screenshot_started sessionId={sessionId} source={target.Source}");
            return ExternalSessionScreenshotCapturePolicy.Host(target.Source, captureRequest.MaxWidth,
                captureRequest.IntervalMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Warning($"external_session_screenshot_probe_failed sessionId={sessionId} reason=\"{ex.Message}\"");
            return ExternalSessionScreenshotCapturePolicy.App($"Ansight external capture probe failed: {ex.Message}");
        }
    }

    public async Task<byte[]?> CaptureFrameAsync(string sessionId, CancellationToken cancellationToken)
    {
        CaptureHandle? handle;
        CancellationTokenSource deadline;
        lock (captureGate)
        {
            if (!capturesBySessionId.TryGetValue(sessionId, out handle)
                || handle.Task?.IsCompleted != false)
                return null;
            deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, handle.Cancellation.Token);
        }
        using (deadline)
        {
            deadline.CancelAfter(captureTimeout);
            var bytes = await handle.Target.CaptureAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            return bytes;
        }
    }

    public IDisposable BeginTestRun(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var normalizedSessionId = sessionId.Trim();
        CaptureHandle handle;
        lock (captureGate)
        {
            if (!capturesBySessionId.TryGetValue(normalizedSessionId, out handle!)
                || handle.Task is null
                || handle.Task.IsCompleted)
            {
                if (CanUseAppManagedCapture(normalizedSessionId))
                {
                    log.Info($"app_managed_screenshot_test_profile_started sessionId={normalizedSessionId}");
                    return AppManagedTestRunScope.Instance;
                }

                throw new InvalidOperationException(
                    "Tests require active screenshot capture for the selected target.");
            }

            handle.TestRunCount++;
            handle.SignalProfileChanged();
        }

        log.Info($"external_session_screenshot_test_profile_started sessionId={normalizedSessionId}");
        return new TestRunScope(this, normalizedSessionId);
    }

    private bool CanUseAppManagedCapture(string sessionId)
    {
        return runtimeState.TryGetSessionSnapshot(sessionId, out var snapshot)
               && snapshot is { IsHistorical: false }
               && (snapshot.CaptureSource == WorkspaceExecutionModes.Sdk
                   && (snapshot.DeviceProfile is not null || !string.IsNullOrWhiteSpace(snapshot.DeviceProfileJson))
                   || snapshot.DeviceProfile?.Device is { } device
                   && device.IsVirtual != true
                   && device.IsEmulator != true);
    }

    public void SetInterval(string sessionId, int intervalMilliseconds)
    {
        AppWatchDefinition.ValidateScreenshotInterval(intervalMilliseconds);
        lock (captureGate)
        {
            if (!capturesBySessionId.TryGetValue(sessionId, out var handle)) return;
            handle.CaptureRequest = handle.CaptureRequest with { IntervalMilliseconds = intervalMilliseconds };
            handle.SignalProfileChanged();
        }
    }

    public async Task StopAsync(string sessionId, string reason)
    {
        CaptureHandle? handle;
        lock (captureGate)
        {
            if (!capturesBySessionId.Remove(sessionId, out handle))
            {
                return;
            }
        }

        handle.Cancellation.Cancel();
        if (handle.Task is not null && Task.CurrentId != handle.Task.Id)
        {
            try
            {
                await handle.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is an expected completion path.
            }
        }

        handle.Cancellation.Dispose();
        handle.ProfileChanged.Dispose();
        log.Info($"external_session_screenshot_stopped sessionId={sessionId} reason=\"{reason}\"");
    }

    private async Task RunCapturePumpAsync(string sessionId, CaptureHandle handle)
    {
        var consecutiveFailures = 0;
        var lastCaptureStarted = Stopwatch.GetTimestamp();
        try
        {
            while (!handle.Cancellation.IsCancellationRequested)
            {
                var profile = ResolveCaptureProfile(handle);
                var remaining = profile.Interval - Stopwatch.GetElapsedTime(lastCaptureStarted);
                if (await handle.ProfileChanged
                        .WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, handle.Cancellation.Token)
                        .ConfigureAwait(false))
                {
                    continue;
                }

                if (runtimeState.TryGetSessionSnapshot(sessionId, out var snapshot)
                    && snapshot?.AppState == global::Ansight.AppLifecycleState.Background)
                {
                    lastCaptureStarted = Stopwatch.GetTimestamp();
                    continue;
                }

                try
                {
                    lastCaptureStarted = Stopwatch.GetTimestamp();
                    using var captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        handle.Cancellation.Token);
                    captureCancellation.CancelAfter(captureTimeout);
                    var bytes = await handle.Target
                        .CaptureAsync(captureCancellation.Token)
                        .ConfigureAwait(false);
                    AddFrame(sessionId, bytes, ResolveCaptureProfile(handle));
                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException) when (handle.Cancellation.IsCancellationRequested)
                {
                    break;
                }
                catch (AppBackgroundedException ex)
                {
                    // A foreground guard can observe Home before the app-watch polling cycle.
                    // Preserve that observation and let the watch confirm and finalize the session.
                    runtimeState.SetSessionAppState(sessionId, global::Ansight.AppLifecycleState.Background,
                        ex.ObservedAtUtc);
                    consecutiveFailures = 0;
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    log.Warning($"external_session_screenshot_failed sessionId={sessionId} count={consecutiveFailures} reason=\"{ex.Message}\"");
                    if (consecutiveFailures >= maximumConsecutiveFailures)
                    {
                        if (IsTestRunActive(handle) && !runtimeState.IsDeviceSessionActive(sessionId))
                        {
                            continue;
                        }

                        if (runtimeState.IsDeviceSessionActive(sessionId))
                        {
                            runtimeState.EndDeviceSession(sessionId);
                            runtimeState.SetSessionStatus(sessionId, "Failed", "Host screenshot capture failed.");
                        }
                        CaptureFailed?.Invoke(
                            this,
                            new ExternalSessionScreenshotCaptureFailedEventArgs(
                                sessionId,
                                $"Ansight external screenshot capture failed {consecutiveFailures} times: {ex.Message}"));
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (handle.Cancellation.IsCancellationRequested)
        {
        }
    }

    private bool IsTestRunActive(CaptureHandle handle)
    {
        lock (captureGate)
        {
            return handle.TestRunCount > 0;
        }
    }

    private ExternalSessionScreenshotCaptureProfile ResolveCaptureProfile(CaptureHandle handle)
    {
        lock (captureGate)
        {
            return handle.TestRunCount > 0
                ? ExternalSessionScreenshotCaptureProfile.TestRun(handle.CaptureRequest)
                : ExternalSessionScreenshotCaptureProfile.Standard(handle.CaptureRequest);
        }
    }

    private void AddFrame(
        string sessionId,
        byte[] capturedBytes,
        ExternalSessionScreenshotCaptureProfile profile)
    {
        var encoded = ExternalSessionScreenshotFrameEncoder.Encode(capturedBytes, profile);
        runtimeState.AddSessionImage(
            sessionId,
            DateTimeOffset.UtcNow,
            encoded.Format,
            encoded.Width,
            encoded.Height,
            encoded.Quality,
            encoded.Bytes);
    }

    private void EndTestRun(string sessionId)
    {
        lock (captureGate)
        {
            if (!capturesBySessionId.TryGetValue(sessionId, out var handle)
                || handle.TestRunCount <= 0)
            {
                return;
            }

            handle.TestRunCount--;
            handle.SignalProfileChanged();
        }

        log.Info($"external_session_screenshot_test_profile_stopped sessionId={sessionId}");
    }

    private async Task<CaptureTarget?> ResolveTargetAsync(
        DeviceAppProfile profile,
        string? profileJson,
        CancellationToken cancellationToken)
    {
        var osName = profile.Device?.OsName?.Trim().ToLowerInvariant();
        if (string.Equals(osName, "android", StringComparison.Ordinal))
        {
            return await ResolveAndroidTargetAsync(profile, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(osName, "ios", StringComparison.Ordinal))
        {
            return await ResolveAppleTargetAsync(profile, profileJson, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<CaptureTarget?> ResolveDeviceRunTargetAsync(
        DeviceAppProfile profile, string? profileJson, CancellationToken cancellationToken)
    {
        var identifier = ResolveNativeDeviceId(profileJson);
        if (identifier is null) return null;
        if (string.Equals(profile.Device?.OsName, "android", StringComparison.OrdinalIgnoreCase))
        {
            var tool = AdbToolLocator.Resolve(userPreferences.AdbPath);
            if (!tool.IsFound) return null;
            var client = new AdbClient(tool.AdbPath);
            var devices = await client.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
            var physical = profile.Device?.IsVirtual != true && profile.Device?.IsEmulator != true;
            var commands = new DeviceCommandRunner();
            return HasConnectedAndroidDevice(devices, identifier)
                ? new CaptureTarget("adb", async token =>
                {
                    if (physical)
                    {
                        var activities = await commands.RunAsync(tool.AdbPath, ["-s", identifier, "shell",
                            "dumpsys activity activities"], token).ConfigureAwait(false);
                        if (!AndroidForegroundActivity.IsForeground(activities.RequireText(), profile.App?.AppId ?? string.Empty))
                            throw new AppBackgroundedException(DateTimeOffset.UtcNow);
                    }
                    return await client.CaptureScreenshotPngAsync(identifier, token).ConfigureAwait(false);
                })
                : null;
        }
        var simctl = await SimCtlToolLocator.ResolveAsync(userPreferences.XcodePath, cancellationToken).ConfigureAwait(false);
        if (!simctl.IsFound) return null;
        var simulator = new SimCtlClient(simctl);
        var simulators = await simulator.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        return simulators.Any(device => device.IsBooted && string.Equals(device.Udid, identifier, StringComparison.OrdinalIgnoreCase))
            ? new CaptureTarget("simctl", token => simulator.CaptureScreenshotJpegAsync(identifier, token))
            : null;
    }

    internal static bool HasConnectedAndroidDevice(IReadOnlyList<AdbDevice> devices, string identifier)
        => devices.Any(device => device.IsConnected
            && string.Equals(device.Serial, identifier, StringComparison.Ordinal));

    internal sealed class AppBackgroundedException(DateTimeOffset observedAtUtc)
        : IOException("The watched Android app is no longer in the foreground.")
    {
        public DateTimeOffset ObservedAtUtc { get; } = observedAtUtc;
    }

    private async Task<CaptureTarget?> ResolveAndroidTargetAsync(
        DeviceAppProfile profile,
        CancellationToken cancellationToken)
    {
        var packageIdentifier = profile.App?.AppId?.Trim();
        if (string.IsNullOrWhiteSpace(packageIdentifier))
        {
            return null;
        }

        var tool = AdbToolLocator.Resolve(userPreferences.AdbPath);
        if (!tool.IsFound)
        {
            return null;
        }

        var client = new AdbClient(tool.AdbPath);
        var reportedProcessId = profile.App?.ProcessId;
        var matches = new List<AdbDevice>();
        foreach (var device in (await client.GetDevicesAsync(cancellationToken).ConfigureAwait(false))
                     .Where(device => device.IsConnected))
        {
            var processIds = await client.GetProcessIdsAsync(
                device.Serial,
                packageIdentifier,
                cancellationToken).ConfigureAwait(false);
            if (processIds.Count > 0
                && (!reportedProcessId.HasValue || processIds.Contains(reportedProcessId.Value)))
            {
                matches.Add(device);
            }
        }

        return matches.Count == 1
            ? new CaptureTarget(
                "adb",
                token => client.CaptureScreenshotPngAsync(matches[0].Serial, token))
            : null;
    }

    private async Task<CaptureTarget?> ResolveAppleTargetAsync(
        DeviceAppProfile profile,
        string? profileJson,
        CancellationToken cancellationToken)
    {
        var processId = profile.App?.ProcessId;
        if (!processId.HasValue || processId.Value <= 0)
        {
            return null;
        }

        var tool = await SimCtlToolLocator.ResolveAsync(
            userPreferences.XcodePath,
            cancellationToken).ConfigureAwait(false);
        if (!tool.IsFound)
        {
            return null;
        }

        var client = new SimCtlClient(tool);
        var bootedDevices = (await client.GetDevicesAsync(cancellationToken).ConfigureAwait(false))
            .Where(device => device.IsBooted)
            .ToArray();
        var nativeDeviceId = ResolveNativeDeviceId(profileJson);
        if (!string.IsNullOrWhiteSpace(nativeDeviceId))
        {
            var explicitTarget = bootedDevices.FirstOrDefault(
                device => string.Equals(device.Udid, nativeDeviceId, StringComparison.OrdinalIgnoreCase));
            return explicitTarget is not null
                   && await client.ProcessExistsAsync(explicitTarget.Udid, processId.Value, cancellationToken).ConfigureAwait(false)
                ? new CaptureTarget(
                    "simctl",
                    token => client.CaptureScreenshotJpegAsync(explicitTarget.Udid, token))
                : null;
        }

        var matches = new List<SimCtlDevice>();
        foreach (var device in bootedDevices)
        {
            if (await client.ProcessExistsAsync(device.Udid, processId.Value, cancellationToken).ConfigureAwait(false))
            {
                matches.Add(device);
            }
        }

        return matches.Count == 1
            ? new CaptureTarget(
                "simctl",
                token => client.CaptureScreenshotJpegAsync(matches[0].Udid, token))
            : null;
    }

    private static string? ResolveNativeDeviceId(string? profileJson)
    {
        if (string.IsNullOrWhiteSpace(profileJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(profileJson);
            return document.RootElement.TryGetProperty("device", out var device)
                   && device.ValueKind == JsonValueKind.Object
                ? JsonUtil.TryGetString(device, "nativeDeviceId")?.Trim()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class CaptureHandle(
        CancellationTokenSource cancellation,
        CaptureTarget target,
        ExternalSessionScreenshotCaptureRequest captureRequest)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;

        public CaptureTarget Target { get; } = target;

        public ExternalSessionScreenshotCaptureRequest CaptureRequest { get; set; } = captureRequest;

        public SemaphoreSlim ProfileChanged { get; } = new(0, 1);

        public int TestRunCount { get; set; }

        public Task? Task { get; set; }

        public void SignalProfileChanged()
        {
            if (ProfileChanged.CurrentCount == 0)
            {
                ProfileChanged.Release();
            }
        }
    }

    private sealed class AppManagedTestRunScope : IDisposable
    {
        public static IDisposable Instance { get; } = new AppManagedTestRunScope();

        public void Dispose()
        {
        }
    }

    private sealed class CaptureTarget(
        string source,
        Func<CancellationToken, Task<byte[]>> captureAsync)
    {
        public string Source { get; } = source;

        private readonly SemaphoreSlim captureLock = new(1, 1);

        public async Task<byte[]> CaptureAsync(CancellationToken cancellationToken)
        {
            await captureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return await captureAsync(cancellationToken).ConfigureAwait(false); }
            finally { captureLock.Release(); }
        }
    }

    private sealed class TestRunScope(
        ExternalSessionScreenshotCaptureManager owner,
        string sessionId) : IDisposable
    {
        private ExternalSessionScreenshotCaptureManager? owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref owner, null)?.EndTestRun(sessionId);
        }
    }
}
