using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Ansight.Host.Audio.Android;
using Ansight.Host.Audio.Ios;

namespace Ansight.Host.Audio;

internal sealed class AudioInjectionEngine : IAsyncDisposable
{
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Func<string, CancellationToken, Task<AudioTarget>> resolveTarget;
    private readonly Func<string, bool> sessionConnected;
    private readonly Action<AudioTarget, JsonObject, string> persist;
    private readonly IReadOnlyList<IAudioInjectionProvider> providers;
    private readonly string temporaryRoot;
    private readonly CancellationToken lifetime;
    private readonly IosAudioRouteRecovery? iosAudioRecovery;
    private readonly ConcurrentDictionary<string, ActiveInjection> active = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private bool disposed;
    private bool stopping;

    public AudioInjectionEngine(IRuntimeState runtimeState, IAppToolBridge appToolBridge, DeviceService devices,
        RuntimeOptions options, IApplicationPaths paths)
        : this(
            (session, token) => ResolveTargetAsync(runtimeState, appToolBridge, devices, session, token),
            appToolBridge.IsSessionConnected,
            (target, report, directory) => Persist(runtimeState, target, report, directory),
            [new AndroidAudioInjectionProvider(options), new IosAudioInjectionProvider(options)],
            Path.Combine(paths.ApplicationDataPath, "temp", "audio-injection"), options.FeatureLifetime,
            new IosAudioRouteRecovery(paths.ApplicationDataPath))
    {
    }

    internal AudioInjectionEngine(Func<string, CancellationToken, Task<AudioTarget>> resolveTarget,
        Func<string, bool> sessionConnected, Action<AudioTarget, JsonObject, string> persist,
        IReadOnlyList<IAudioInjectionProvider> providers, string temporaryRoot, CancellationToken lifetime = default, IosAudioRouteRecovery? iosAudioRecovery = null)
    {
        this.resolveTarget = resolveTarget;
        this.sessionConnected = sessionConnected;
        this.persist = persist;
        this.providers = providers;
        this.temporaryRoot = temporaryRoot;
        this.lifetime = lifetime;
        this.iosAudioRecovery = iosAudioRecovery;
    }

    public async Task<JsonObject> GetCapabilitiesAsync(string sessionId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (stopping) throw new AudioInjectionException("host-stopping", "The host audio service is stopping.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var target = await resolveTarget(sessionId, timeout.Token).ConfigureAwait(false);
        var provider = GetProvider(target);
        var capability = await provider.GetCapabilitiesAsync(target, timeout.Token).ConfigureAwait(false);
        if (target.Platform == "ios" && capability.Code == "simulator-audio-route-stale")
        {
            using var lease = AudioRouteLease.Acquire(provider.GetLeaseKey(target));
            iosAudioRecovery?.Record(target);
        }
        return new JsonObject
        {
            ["schema"] = "ansight.audio-capabilities/v1",
            ["available"] = capability.Available,
            ["code"] = capability.Code,
            ["message"] = capability.Message,
            ["sessionId"] = target.SessionId,
            ["deviceId"] = target.DeviceId,
            ["platform"] = target.Platform,
            ["backend"] = provider.Backend,
            ["routingScope"] = RoutingScope(target),
            ["limits"] = new JsonObject
            {
                ["maximumDurationMs"] = AudioFixtureLoader.MaximumDurationMs,
                ["maximumFileBytes"] = AudioFixtureLoader.MaximumFileBytes,
                ["sampleRate"] = 16000, ["channels"] = 1, ["bitsPerSample"] = 16,
                ["maximumTimeoutMs"] = 60000, ["maximumWaitForMicrophoneMs"] = 10000
            },
            ["diagnostics"] = capability.Diagnostics.DeepClone()
        };
    }

    public async Task<JsonObject> InjectAsync(string sessionId, string file, int timeoutMs,
        int waitForMicrophoneMs, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (timeoutMs is < 100 or > 60000 || waitForMicrophoneMs is < 0 or > 10000)
            throw new AudioInjectionException("invalid-arguments", "timeoutMs must be 100–60000 and waitForMicrophoneMs must be 0–10000.");

        var operationId = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(temporaryRoot, operationId);
        var report = new JsonObject
        {
            ["schema"] = "ansight.audio-injection/v1", ["operationId"] = operationId,
            ["sessionId"] = sessionId, ["status"] = "failed", ["captureVerified"] = false,
            ["transcriptionVerified"] = false, ["requestedUtc"] = DateTimeOffset.UtcNow,
            ["deliveryStarted"] = false
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime, deadline.Token);
        var injection = new ActiveInjection(cancelled);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (stopping) throw new AudioInjectionException("host-stopping", "The host is stopping; no audio was injected.");
            if (!active.TryAdd(operationId, injection)) throw new InvalidOperationException("Duplicate audio operation identity.");
        }
        AudioTarget? target = null;
        AudioRouteLease? lease = null;
        Task? disconnectMonitor = null;
        using var monitorLifetime = new CancellationTokenSource();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var providerEntered = false;
        try
        {
            Directory.CreateDirectory(directory);
            var fixture = await AudioFixtureLoader.LoadAsync(file, Path.Combine(directory, "fixture.wav"), cancelled.Token).ConfigureAwait(false);
            report["fixture"] = JsonSerializer.SerializeToNode(fixture.Info, jsonOptions);
            target = await resolveTarget(sessionId, cancelled.Token).ConfigureAwait(false);
            report["deviceId"] = target.DeviceId;
            report["platform"] = target.Platform;
            var provider = GetProvider(target);
            report["backend"] = provider.Backend;
            lease = AudioRouteLease.Acquire(provider.GetLeaseKey(target));
            disconnectMonitor = MonitorSessionAsync(target.SessionId, injection, monitorLifetime.Token);
            cancelled.Token.ThrowIfCancellationRequested();
            if (!sessionConnected(target.SessionId))
                throw new AudioInjectionException("session-not-connected", "The selected session disconnected before injection.");
            if (timeoutMs - System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds < fixture.Info.DurationMs)
                throw new AudioInjectionException("timeout-too-short", "The remaining timeout is shorter than the audio fixture. No audio was injected.");

            var providerStarted = DateTimeOffset.UtcNow;
            providerEntered = true;
            // Providers perform the final readiness check immediately before their first sample.
            var delivery = await provider.InjectAsync(target, fixture, waitForMicrophoneMs, cancelled.Token).ConfigureAwait(false);
            report["deliveryStarted"] = true;
            report["delivery"] = new JsonObject
            {
                ["completionKind"] = delivery.CompletionKind,
                ["submittedFrames"] = delivery.SubmittedFrames,
                ["providerStartedUtc"] = providerStarted,
                ["completedUtc"] = DateTimeOffset.UtcNow,
                ["routingScope"] = RoutingScope(target),
                ["diagnostics"] = delivery.Diagnostics.DeepClone()
            };
            cancelled.Token.ThrowIfCancellationRequested();
            if (delivery.SubmittedFrames != fixture.Info.FrameCount)
                throw new AudioInjectionException("delivery-incomplete", "The provider did not submit the complete fixture.");
            report["status"] = "completed";
            report["message"] = "Audio delivery completed. App microphone capture and transcription require separate assertions.";
        }
        catch (OperationCanceledException)
        {
            report["status"] = "cancelled";
            report["code"] = injection.SessionDisconnected ? "session-disconnected" : deadline.IsCancellationRequested ? "timeout" : "cancelled";
            report["message"] = injection.SessionDisconnected ? "The selected session disconnected; audio delivery was cancelled."
                : deadline.IsCancellationRequested ? "Audio delivery timed out and was cancelled." : "Audio delivery was cancelled.";
            report["deliveryMayBePartial"] = providerEntered;
            if (providerEntered) report["deliveryStarted"] = null;
        }
        catch (AudioInjectionException exception)
        {
            if (target?.Platform == "ios" && exception.Code == "simulator-audio-route-stale")
            {
                try { iosAudioRecovery?.Record(target); }
                catch (Exception recoveryError) when (recoveryError is IOException or UnauthorizedAccessException)
                {
                    report["recoveryWarning"] = $"Could not save restart requirement: {recoveryError.Message}. Restart the simulator manually before a fresh run.";
                }
            }
            report["code"] = exception.Code;
            report["message"] = exception.Message;
            report["diagnostics"] = exception.Diagnostics?.DeepClone();
            var deliveryStarted = report["deliveryStarted"]?.GetValue<bool>() == true
                ? true : exception.Diagnostics?["deliveryStarted"]?.GetValue<bool>();
            report["deliveryStarted"] = deliveryStarted ?? (providerEntered ? null : false);
            report["deliveryMayBePartial"] = deliveryStarted ?? providerEntered;
        }
        catch (Exception exception)
        {
            report["code"] = "audio-operation-failed";
            report["message"] = exception.Message;
            report["deliveryMayBePartial"] = providerEntered;
            if (providerEntered) report["deliveryStarted"] = null;
        }
        finally
        {
            try
            {
                monitorLifetime.Cancel();
                if (disconnectMonitor is not null) await disconnectMonitor.ConfigureAwait(false);
                lease?.Dispose();
                report["completedUtc"] = DateTimeOffset.UtcNow;
                report["elapsedMs"] = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                // Delete the fixture before persisting the metadata-only report.
                try { File.Delete(Path.Combine(directory, "fixture.wav")); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    report["deliveryCompleted"] = report["status"]?.GetValue<string>() == "completed";
                    report["status"] = "failed";
                    report["code"] = "cleanup-failed";
                    report["message"] = "Temporary audio cleanup failed. Do not automatically replay the fixture.";
                }
                if (target is not null)
                {
                    try
                    {
                        report["evidenceId"] = $"audio-{operationId}";
                        persist(target, report, directory);
                    }
                    catch (Exception exception)
                    {
                        report.Remove("evidenceId");
                        report["deliveryCompleted"] = report["status"]?.GetValue<string>() == "completed";
                        report["status"] = "failed";
                        report["code"] = "evidence-persistence-failed";
                        report["message"] = $"Audio outcome could not be persisted: {exception.Message}. Do not automatically replay the fixture.";
                    }
                }
                try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The waveform was already removed above; leftover report metadata is harmless.
                }
            }
            finally
            {
                try { lease?.Dispose(); }
                finally
                {
                    active.TryRemove(operationId, out _);
                    injection.Completion.TrySetResult();
                }
            }
        }
        return report;
    }

    private async Task MonitorSessionAsync(string sessionId, ActiveInjection injection, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!sessionConnected(sessionId))
                {
                    injection.SessionDisconnected = true;
                    await injection.Cancellation.CancelAsync().ConfigureAwait(false);
                    return;
                }
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception)
        {
            injection.SessionDisconnected = true;
            await injection.Cancellation.CancelAsync().ConfigureAwait(false);
        }
    }

    public async Task StopAsync()
    {
        ActiveInjection[] operations;
        lock (gate)
        {
            stopping = true;
            operations = active.Values.ToArray();
        }
        foreach (var operation in operations)
        {
            try { await operation.Cancellation.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        }
        await Task.WhenAll(operations.Select(operation => operation.Completion.Task)).ConfigureAwait(false);
    }

    public void Start()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            stopping = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        await StopAsync().ConfigureAwait(false);
        foreach (var provider in providers)
        {
            if (provider is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else if (provider is IDisposable disposable) disposable.Dispose();
        }
    }

    private IAudioInjectionProvider GetProvider(AudioTarget target)
        => providers.SingleOrDefault(provider => string.Equals(provider.Platform, target.Platform, StringComparison.OrdinalIgnoreCase))
           ?? throw new AudioInjectionException("unsupported-device", "Audio injection supports Android emulators and iOS Simulators only.");

    private static string RoutingScope(AudioTarget target)
        => target.Platform == "ios" ? "shared-host-audio-route" : "emulator";

    private static async Task<AudioTarget> ResolveTargetAsync(IRuntimeState state, IAppToolBridge bridge, DeviceService devices,
        string sessionId, CancellationToken cancellationToken)
    {
        if (!bridge.IsSessionConnected(sessionId) || !state.TryGetSessionSnapshot(sessionId, out var snapshot) || snapshot is null)
            throw new AudioInjectionException("session-not-connected", "Provide an existing connected Ansight session.");
        var nativeId = DeviceLifecycleTool.ResolveNativeDeviceIdentifier(snapshot);
        if (string.IsNullOrWhiteSpace(nativeId))
            throw new AudioInjectionException("unsupported-device", "The session has no authoritative native device identity.");
        var inventory = await devices.ListAsync(cancellationToken).ConfigureAwait(false);
        var matches = inventory.Devices.Where(device => string.Equals(device.Identifier, nativeId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || !matches[0].IsVirtual || !matches[0].IsBooted || !matches[0].IsAvailable)
            throw new AudioInjectionException("unsupported-device", "The session must resolve to exactly one running, available simulator or emulator; physical devices are unsupported.");
        if (!bridge.IsSessionConnected(sessionId))
            throw new AudioInjectionException("session-not-connected", "The selected session disconnected while its device was resolved.");
        return new AudioTarget(sessionId, snapshot.AppId, nativeId, matches[0].Platform.ToLowerInvariant());
    }

    internal static void Persist(IRuntimeState state, AudioTarget target, JsonObject report, string directory)
    {
        var evidenceId = report["evidenceId"]!.GetValue<string>();
        var path = Path.Combine(directory, "delivery.json");
        File.WriteAllText(path, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var evidenceDirectory = Path.Combine(directory, "evidence");
        Directory.CreateDirectory(evidenceDirectory);
        File.Copy(path, Path.Combine(evidenceDirectory, "delivery.json"));
        var snapshot = new SessionArtifactSnapshot
        {
            SnapshotId = evidenceId, CapturedAtUtc = DateTimeOffset.UtcNow, Source = "host.audio-injection",
            RootAlias = "audio-injection", RelativePath = evidenceId, Name = "Audio injection delivery", Kind = "audio-injection",
            ArtifactDirectoryName = evidenceId,
            Entries = [new SessionArtifactEntry
            {
                Name = "delivery.json", RootAlias = "audio-injection", RelativePath = "delivery.json", SnapshotRelativePath = "delivery.json",
                Kind = "file", SizeBytes = new FileInfo(path).Length, FileExtension = ".json", MimeType = "application/json",
                ArchiveRelativePath = "delivery.json"
            }]
        };
        var result = state.AddSessionArtifactSnapshot(target.SessionId, snapshot, evidenceDirectory);
        if (!result.IsSuccess) throw new InvalidOperationException(result.Message);
    }

    private sealed class ActiveInjection(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SessionDisconnected { get; set; }
    }
}
