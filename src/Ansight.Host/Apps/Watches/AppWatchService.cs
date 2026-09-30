using System.Text.RegularExpressions;

namespace Ansight.Host.Apps;

/// <summary>Persistent app watches discover supported device processes while the resident host is running.</summary>
public sealed class AppWatchService
{
    private readonly Lock gate = new();
    private readonly SemaphoreSlim cycle = new(1, 1);
    private readonly string storePath;
    private readonly IAppWatchBackend backend;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private CancellationTokenSource? lifetime;
    private Task? pump;

    internal AppWatchService(string dataDirectory, IAppWatchBackend backend)
    {
        storePath = Path.Combine(dataDirectory, "app-watches.json");
        this.backend = backend;
        if (!File.Exists(storePath)) return;
        var watches = JsonSerializer.Deserialize<AppWatchDefinition[]>(File.ReadAllText(storePath), JsonUtil.Compact)
            ?? throw new InvalidDataException($"Invalid app watch configuration: {storePath}");
        foreach (var watch in watches)
        {
            Validate(watch.AppId, watch.Platform, watch.DeviceId, watch.CaptureFiles);
            AppWatchDefinition.ValidateScreenshotInterval(watch.ScreenshotIntervalMilliseconds);
            entries.Add(watch.Id, new Entry(watch));
        }
    }

    public bool IsRunning { get { lock (gate) return pump is { IsCompleted: false }; } }

    public IReadOnlyList<AppWatchStatus> List()
    {
        lock (gate) return entries.Values.Select(entry => entry.Status with
        {
            Watch = entry.Definition with { CaptureFiles = [.. entry.Definition.CaptureFiles] },
            Devices = entry.Status.Devices.ToArray()
        }).ToArray();
    }

    public async Task<AppWatchDefinition> AddAsync(string appId, string? platform, string? deviceId,
        IReadOnlyList<string> captureFiles, CancellationToken cancellationToken = default,
        bool captureInstruments = false, int? screenshotIntervalMilliseconds = null)
    {
        appId = appId.Trim();
        platform = platform?.Trim().ToLowerInvariant();
        deviceId = deviceId?.Trim();
        var files = captureFiles.Distinct(StringComparer.Ordinal).ToArray();
        Validate(appId, platform, deviceId, files);
        if (screenshotIntervalMilliseconds.HasValue)
            AppWatchDefinition.ValidateScreenshotInterval(screenshotIntervalMilliseconds.Value);
        if (captureInstruments && (platform != DevicePlatforms.Ios || string.IsNullOrWhiteSpace(deviceId)))
            throw new ArgumentException("Instruments capture requires --platform ios and an explicit physical iPhone --device-id.");
        await cycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (gate)
            {
                var existing = entries.Values.FirstOrDefault(entry => entry.Definition.AppId == appId
                    && entry.Definition.Platform == platform && entry.Definition.DeviceId == deviceId);
                var definition = new AppWatchDefinition(existing?.Definition.Id ?? Guid.NewGuid().ToString("N"),
                    appId, platform, deviceId, true, files, captureInstruments,
                    screenshotIntervalMilliseconds ?? existing?.Definition.ScreenshotIntervalMilliseconds
                        ?? AppWatchDefinition.DefaultScreenshotIntervalMilliseconds);
                Persist(entries.Values.Where(entry => entry != existing).Select(entry => entry.Definition).Append(definition));
                if (existing is null)
                {
                    existing = new Entry(definition);
                    entries.Add(definition.Id, existing);
                }
                else
                {
                    existing.Definition = definition;
                    foreach (var target in existing.Targets.Values)
                        target.Capture?.SetScreenshotInterval(definition.ScreenshotIntervalMilliseconds);
                }
                UpdateStatus(existing, IsRunning ? null : "host-stopped");
                return definition with { CaptureFiles = [.. files] };
            }
        }
        finally { cycle.Release(); }
    }

    public async Task SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default)
    {
        await cycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Entry entry;
            lock (gate)
            {
                entry = Require(id);
                var definition = entry.Definition with { Enabled = enabled };
                Persist(entries.Values.Select(item => item == entry ? definition : item.Definition));
                entry.Definition = definition;
            }
            if (!enabled) await FinishAllAsync(entry, "watch.disabled").ConfigureAwait(false);
            UpdateStatus(entry, enabled ? null : "disabled");
        }
        finally { cycle.Release(); }
    }

    public async Task SetScreenshotIntervalAsync(string id, int intervalMilliseconds,
        CancellationToken cancellationToken = default)
    {
        AppWatchDefinition.ValidateScreenshotInterval(intervalMilliseconds);
        await cycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Entry entry;
            lock (gate)
            {
                entry = Require(id);
                var definition = entry.Definition with { ScreenshotIntervalMilliseconds = intervalMilliseconds };
                Persist(entries.Values.Select(item => item == entry ? definition : item.Definition));
                entry.Definition = definition;
                foreach (var target in entry.Targets.Values)
                    target.Capture?.SetScreenshotInterval(intervalMilliseconds);
            }
            UpdateStatus(entry, entry.Definition.Enabled ? null : "disabled");
        }
        finally { cycle.Release(); }
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await cycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Entry entry;
            lock (gate)
            {
                entry = Require(id);
                Persist(entries.Values.Where(item => item != entry).Select(item => item.Definition));
            }
            await FinishAllAsync(entry, "watch.removed").ConfigureAwait(false);
            lock (gate) entries.Remove(id);
        }
        finally { cycle.Release(); }
    }

    internal void Start(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (pump is { IsCompleted: false }) return;
            lifetime?.Dispose();
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            pump = RunAsync(lifetime.Token);
        }
    }

    internal async Task StopAsync()
    {
        Task? running;
        lock (gate) { lifetime?.Cancel(); running = pump; }
        if (running is not null) await running.ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await PollAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            await cycle.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var entry in entries.Values)
                {
                    await FinishAllAsync(entry, "host.stopped").ConfigureAwait(false);
                    UpdateStatus(entry, entry.Definition.Enabled ? "host-stopped" : "disabled");
                }
            }
            finally { cycle.Release(); }
        }
    }

    internal async Task PollAsync(CancellationToken cancellationToken = default)
    {
        await cycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var entry in entries.Values.Where(entry => entry.Definition.Enabled))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    foreach (var target in entry.Targets.Values.Where(target => target.Capture?.IsActive == false))
                        await FinishAsync(entry, target, "capture.failed").ConfigureAwait(false);
                    var discovery = await backend.ObserveAsync(entry.Definition, cancellationToken).ConfigureAwait(false);
                    entry.DiscoveryError = discovery.Warning;
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var observation in discovery.Observations)
                    {
                        var key = observation.Device.Platform + ":" + observation.Device.Identifier;
                        if (!seen.Add(key)) continue;
                        if (!entry.Targets.TryGetValue(key, out var target))
                            entry.Targets.Add(key, target = new Target(observation.Device));
                        target.Device = observation.Device;
                        try { await ObserveTargetAsync(entry, target, observation, cancellationToken).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception exception) { ProbeFailed(target, exception.Message); }
                        UpdateStatus(entry);
                    }
                    foreach (var target in entry.Targets.Where(item => !seen.Contains(item.Key)).Select(item => item.Value))
                    {
                        // Partial inventory failures are not proof that a previously seen device disappeared.
                        if (discovery.Warning is not null) ProbeFailed(target, discovery.Warning);
                        else await AbsentAsync(entry, target, deviceUnavailable: true).ConfigureAwait(false);
                    }
                    UpdateStatus(entry);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    entry.DiscoveryError = exception.Message;
                    foreach (var target in entry.Targets.Values) ProbeFailed(target, exception.Message);
                    UpdateStatus(entry);
                }
            }
        }
        finally { cycle.Release(); }
    }

    private async Task ObserveTargetAsync(Entry entry, Target target, AppWatchObservation observation, CancellationToken cancellationToken)
    {
        if (target.Capture?.IsActive == false)
            await FinishAsync(entry, target, "capture.failed").ConfigureAwait(false);
        if (observation.Error is not null) { ProbeFailed(target, observation.Error); return; }
        target.Message = null;
        if (observation.ProcessIdentity is null)
        {
            if (observation.AppState == global::Ansight.AppLifecycleState.Background)
                target.Capture?.SetAppState(observation.AppState, observation.ObservedAtUtc ?? DateTimeOffset.UtcNow);
            await AbsentAsync(entry, target, deviceUnavailable: false).ConfigureAwait(false);
            return;
        }
        target.MissingCount = 0;
        if (target.Capture is not null && target.Identity != observation.ProcessIdentity)
            await FinishAsync(entry, target, "process.restarted").ConfigureAwait(false);
        if (target.Capture is null)
        {
            target.Capture = await backend.TryStartAsync(entry.Definition, observation, cancellationToken).ConfigureAwait(false);
            if (target.Capture is null)
            {
                target.State = "waiting-for-device-owner";
                target.Message = "Another capture owns this device. The watch will retry.";
                return;
            }
            target.Identity = observation.ProcessIdentity;
        }
        if (observation.AppState == global::Ansight.AppLifecycleState.Foreground)
            target.Capture.SetAppState(observation.AppState, observation.ObservedAtUtc ?? DateTimeOffset.UtcNow);
        target.State = "capturing";
    }

    private async Task AbsentAsync(Entry entry, Target target, bool deviceUnavailable)
    {
        // Debounce absence independently for every device, including two simulators running the same PID.
        if (target.Capture is not null && ++target.MissingCount < 2) return;
        await FinishAsync(entry, target, deviceUnavailable ? "device.unavailable" : "process.exited").ConfigureAwait(false);
        target.State = deviceUnavailable ? "waiting-for-device" : "waiting-for-app";
        target.Message = null;
    }

    private static void ProbeFailed(Target target, string error)
    {
        target.MissingCount = 0;
        target.State = target.Capture is null ? "error" : "capturing-probe-error";
        target.Message = error;
    }

    private async Task FinishAllAsync(Entry entry, string reason)
    {
        foreach (var target in entry.Targets.Values)
        {
            await FinishAsync(entry, target, reason).ConfigureAwait(false);
            target.State = reason == "host.stopped" ? "host-stopped" : "disabled";
        }
    }

    private async Task FinishAsync(Entry entry, Target target, string reason)
    {
        var capture = target.Capture;
        if (capture is null) return;
        target.State = "stopping";
        target.Message = reason;
        UpdateStatus(entry);
        try
        {
            var files = reason is "process.restarted" or "device.unavailable" ? [] : entry.Definition.CaptureFiles;
            await capture.StopAsync(reason, files).ConfigureAwait(false);
        }
        catch (Exception exception) { target.LastError = $"Finalizing {capture.SessionId}: {exception.Message}"; }
        finally
        {
            entry.LastSessionId = target.LastSessionId = capture.SessionId;
            target.Capture = null;
            target.Identity = null;
            target.MissingCount = 0;
            target.Message = null;
        }
    }

    private void UpdateStatus(Entry entry, string? stateOverride = null)
    {
        var targets = entry.Targets.Values.ToArray();
        var active = targets.Where(target => target.Capture is not null).ToArray();
        var hasError = entry.DiscoveryError is not null || targets.Any(target => target.State is "error" or "capturing-probe-error");
        var state = stateOverride ?? (active.Length > 0 ? hasError ? "capturing-probe-error" : "capturing"
            : hasError ? "error"
            : targets.Any(target => target.State == "waiting-for-device-owner") ? "waiting-for-device-owner"
            : targets.Any(target => target.State == "waiting-for-app") ? "waiting-for-app" : "waiting-for-device");
        if (stateOverride is null && targets.Any(target => target.State == "stopping")) state = "stopping";
        var messages = targets.Select(target => target.Message ?? target.LastError).Append(entry.DiscoveryError)
            .Where(message => message is not null).Distinct().ToArray();
        lock (gate) entry.Status = new AppWatchStatus(entry.Definition, state,
            active.Length == 1 ? active[0].Capture!.SessionId : null, entry.LastSessionId,
            active.Length == 1 ? active[0].Identity : null, messages.Length == 0 ? null : string.Join("; ", messages))
        {
            Devices = targets.Select(target => new AppWatchDeviceStatus(target.Device.Platform, target.Device.Identifier,
                target.Device.Name, target.State, target.Capture?.SessionId, target.LastSessionId,
                target.Identity, target.Message ?? target.LastError)).ToArray()
        };
    }

    private Entry Require(string id) => entries.TryGetValue(id, out var entry) ? entry
        : throw new ArgumentException($"App watch '{id}' was not found.");

    private void Persist(IEnumerable<AppWatchDefinition> watches)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
        var temporary = storePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(watches.ToArray(), JsonUtil.Pretty));
        File.Move(temporary, storePath, overwrite: true);
    }

    private static void Validate(string appId, string? platform, string? deviceId, IReadOnlyList<string> files)
    {
        if (!Regex.IsMatch(appId, @"^[A-Za-z0-9_]+(?:\.[A-Za-z0-9_-]+)+$"))
            throw new ArgumentException("An exact app bundle/package identifier is required.");
        if (platform is not (null or DevicePlatforms.Ios or DevicePlatforms.Android))
            throw new ArgumentException("App watches support virtual devices and explicitly selected physical iOS or Android devices.");
        if (deviceId is not null && (string.IsNullOrWhiteSpace(deviceId) || deviceId.Any(char.IsControl)))
            throw new ArgumentException("If specified, --device-id must be an iOS device ID, Android serial, or AVD name.");
        if (files.Count > 16) throw new ArgumentException("An app watch supports at most 16 exit files.");
        foreach (var file in files) DevicePlatformProbe.ValidateRelativePath(file);
    }

    private sealed class Entry(AppWatchDefinition definition)
    {
        public AppWatchDefinition Definition { get; set; } = definition;
        public AppWatchStatus Status { get; set; } = new(definition, definition.Enabled ? "host-stopped" : "disabled");
        public Dictionary<string, Target> Targets { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? LastSessionId { get; set; }
        public string? DiscoveryError { get; set; }
    }

    private sealed class Target(DeviceDescriptor device)
    {
        public DeviceDescriptor Device { get; set; } = device;
        public string State { get; set; } = "waiting-for-app";
        public string? Message { get; set; }
        public IAppWatchCapture? Capture { get; set; }
        public string? Identity { get; set; }
        public string? LastSessionId { get; set; }
        public string? LastError { get; set; }
        public int MissingCount { get; set; }
    }
}
