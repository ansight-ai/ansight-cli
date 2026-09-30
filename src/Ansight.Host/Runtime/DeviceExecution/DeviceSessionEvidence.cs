using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Ansight.Infrastructure.Preferences;

namespace Ansight.Host.Runtime.DeviceExecution;

[Export(typeof(DeviceSessionEvidence))]
[PartCreationPolicy(CreationPolicy.Shared)]
internal sealed class DeviceSessionEvidence
{
    private readonly IRuntimeState state;
    private readonly IUserPreferences preferences;
    private readonly INativeSessionLogCaptureManager nativeLogs;
    private readonly IDeviceCommandRunner commands;
    private readonly ConcurrentDictionary<string, Capture> captures = new(StringComparer.Ordinal);

    [ImportingConstructor]
    public DeviceSessionEvidence(IRuntimeState state, IUserPreferences preferences, INativeSessionLogCaptureManager nativeLogs)
        : this(state, preferences, nativeLogs, new DeviceCommandRunner()) { }

    internal DeviceSessionEvidence(IRuntimeState state, IUserPreferences preferences,
        INativeSessionLogCaptureManager nativeLogs, IDeviceCommandRunner commands)
    {
        this.state = state;
        this.preferences = preferences;
        this.nativeLogs = nativeLogs;
        this.commands = commands;
    }

    public async Task AttachAsync(string sessionId, WorkspaceTestTarget target, CancellationToken cancellationToken,
        string? expectedProcessIdentity = null)
    {
        var capture = new Capture(new DevicePlatformProbe(target, commands, preferences.AdbPath), expectedProcessIdentity);
        if (!captures.TryAdd(sessionId, capture)) throw new InvalidOperationException("Device evidence is already attached.");
        await InitializeAndSampleAsync(sessionId, capture, cancellationToken).ConfigureAwait(false);
        capture.Work = RunAsync(sessionId, capture);
    }

    public Task StopAllAsync() => Task.WhenAll(captures.Keys.Select(StopAsync));

    private async Task InitializeAndSampleAsync(string sessionId, Capture capture, CancellationToken cancellationToken)
    {
        try
        {
            if (!capture.Initialized)
            {
                await capture.Platform.InitializeAsync(cancellationToken).ConfigureAwait(false);
                capture.Initialized = true;
                UpdateProfile(sessionId, capture.Platform.Metadata, icon: capture.Platform.AppIcon);
                foreach (var name in new[] { "files.read", "files.capture" })
                    SetCapability(sessionId, capture, name, capture.Platform.FileProvider is not null,
                        capture.Platform.FileProvider ?? "external-sandbox", capture.Platform.FileUnavailableReason);
                SetCapability(sessionId, capture, "telemetry.fps", false,
                    capture.Platform.IsAndroid ? "adb-gfxinfo" : "instruments",
                    capture.Platform.IsAndroid ? "Waiting for renderer frame statistics."
                        : "The installed Instruments frame collector does not support this simulator platform.");
            }
            await SampleAsync(sessionId, capture, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await ProcessUnavailableAsync(sessionId, capture, exception.Message).ConfigureAwait(false);
            if (!capture.Initialized)
                foreach (var name in new[] { "files.read", "files.capture" })
                    SetCapability(sessionId, capture, name, false, "external-sandbox", exception.Message);
        }
    }

    private async Task ProcessUnavailableAsync(string sessionId, Capture capture, string reason)
    {
        if (capture.Previous is not null)
            await nativeLogs.StopAsync(sessionId, "The selected app process is unavailable.").ConfigureAwait(false);
        capture.Previous = null;
        capture.LastFrame = 0;
        SetCapability(sessionId, capture, "telemetry.process.cpu", false, capture.Platform.Provider, reason);
        SetCapability(sessionId, capture, "telemetry.process.memory", false, capture.Platform.Provider, reason);
        if (capture.Platform.IsAndroid)
            SetCapability(sessionId, capture, "telemetry.fps", false, "adb-gfxinfo", reason);
    }

    public async Task StopAsync(string sessionId)
    {
        if (!captures.TryRemove(sessionId, out var capture)) return;
        await capture.Cancellation.CancelAsync().ConfigureAwait(false);
        try { await capture.Work.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally
        {
            capture.Cancellation.Dispose();
            await nativeLogs.StopAsync(sessionId, "Device capture completed.").ConfigureAwait(false);
        }
    }

    public bool IsCapturing(string sessionId) => captures.ContainsKey(sessionId);

    public DevicePlatformProbe RequireFiles(string sessionId)
    {
        if (!captures.TryGetValue(sessionId, out var capture))
            throw new InvalidOperationException("Live sandbox access requires an active device session.");
        return capture.Platform;
    }

    private async Task RunAsync(string sessionId, Capture capture)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(capture.Cancellation.Token).ConfigureAwait(false))
        {
            try { await InitializeAndSampleAsync(sessionId, capture, capture.Cancellation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (capture.Cancellation.IsCancellationRequested) { break; }
        }
    }

    private async Task SampleAsync(string sessionId, Capture capture, CancellationToken cancellationToken)
    {
        var sample = await capture.Platform.SampleAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var tick = Stopwatch.GetTimestamp();
        if (sample is null)
        {
            await ProcessUnavailableAsync(sessionId, capture,
                "The app process is not running or cannot be identified uniquely.").ConfigureAwait(false);
            return;
        }
        if (capture.ExpectedProcessIdentity is { } expected && expected != sample.Identity)
        {
            await ProcessUnavailableAsync(sessionId, capture, "The watched process ended; waiting for a new recording.").ConfigureAwait(false);
            return;
        }
        var sameProcess = capture.Previous?.Identity == sample.Identity;
        if (!sameProcess)
        {
            capture.Segment++;
            capture.LastFrame = 0;
            UpdateProfile(sessionId, capture.Platform.Metadata, sample.ProcessId);
            if (state.TryGetSessionSnapshot(sessionId, out var profile))
                await nativeLogs.AttachAsync(sessionId, profile!.DeviceProfile, profile.DeviceProfileJson).ConfigureAwait(false);
        }
        var elapsed = sameProcess ? Stopwatch.GetElapsedTime(capture.PreviousTick, tick).TotalSeconds : 0;
        var metrics = new List<SessionMetricSample>();
        Add("rss", "Process RSS", "memory", "bytes", sample.ResidentBytes);
        Add("physical-footprint", "Process physical footprint", "memory", "bytes", sample.FootprintBytes);
        Add("pss", "Process PSS", "memory", "bytes", sample.ProportionalBytes);
        // Integer millicores preserve sub-percent precision without changing the long-valued archive format.
        if (sameProcess && elapsed > 0 && sample.CpuNanoseconds is { } cpu && capture.Previous?.CpuNanoseconds is { } previousCpu && cpu >= previousCpu)
            Add("cpu-millicores", "Process CPU", "cpu", "millicores", (long)Math.Round((cpu - previousCpu) / (elapsed * 1_000_000)));
        SetCapability(sessionId, capture, "telemetry.process.cpu", sample.CpuNanoseconds is not null, capture.Platform.Provider,
            sample.CpuNanoseconds is null ? "Process CPU counters are not readable." : null);
        SetCapability(sessionId, capture, "telemetry.process.memory", sample.ResidentBytes is not null || sample.ProportionalBytes is not null,
            capture.Platform.Provider, sample.ResidentBytes is null && sample.ProportionalBytes is null ? "Process memory counters are not readable." : null);
        if (capture.Platform.IsAndroid)
        {
            try
            {
                var frames = AndroidFrameStatistics.Read(await capture.Platform.ReadFramesAsync(cancellationToken).ConfigureAwait(false), capture.LastFrame, elapsed);
                capture.LastFrame = frames.LastCompletedNanoseconds;
                SetCapability(sessionId, capture, "telemetry.fps", frames.Supported && !frames.Overflow, "adb-gfxinfo",
                    frames.Overflow ? "Frame history overflowed between polls." : frames.Supported ? null : "The renderer does not expose gfxinfo frame statistics.");
                Add("rendered-fps", "Rendered frames per second", "fps", "fps", frames.FramesPerSecond, "adb-gfxinfo-v1");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                capture.LastFrame = 0;
                SetCapability(sessionId, capture, "telemetry.fps", false, "adb-gfxinfo", exception.Message);
            }
        }
        state.AddSessionMetrics(sessionId, metrics, capture.Segment);
        capture.Previous = sample;
        capture.PreviousTick = tick;

        void Add(string kind, string name, string type, string unit, long? value, string? provider = null)
        {
            if (value is null) return;
            if (!capture.Channels.TryGetValue(kind, out var channel))
            {
                channel = HostSessionEvents.AllocateChannel(state, sessionId, name, type, unit, provider ?? capture.Platform.Provider, kind);
                capture.Channels.Add(kind, channel);
            }
            metrics.Add(new SessionMetricSample { ChannelId = channel, Value = value.Value, CapturedAtUtc = now });
        }
    }

    private void SetCapability(string sessionId, Capture capture, string name, bool available, string provider, string? reason)
    {
        var value = new JsonObject { ["available"] = available, ["provider"] = provider, ["reason"] = reason };
        var serialized = value.ToJsonString();
        if (capture.LastCapabilities.GetValueOrDefault(name) == serialized) return;
        capture.LastCapabilities[name] = serialized;
        if (!state.TryGetSessionSnapshot(sessionId, out var session)) return;
        var properties = session!.CustomProperties?.DeepClone().AsObject() ?? new JsonObject();
        var execution = properties["deviceExecution"] as JsonObject ?? new JsonObject();
        var capabilities = execution["capabilities"] as JsonObject ?? new JsonObject();
        capabilities[name] = value;
        execution["capabilities"] = capabilities.Parent is null ? capabilities : capabilities.DeepClone();
        execution["collectorVersion"] = "external-v1";
        execution["sampleIntervalMs"] = 1000;
        execution["cpuNormalization"] = "1000 millicores = one fully utilized logical CPU";
        execution["processScope"] = "main application process";
        execution["hostArchitecture"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();
        execution["hostProcessors"] = Environment.ProcessorCount;
        execution["hostOs"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        execution["deviceMetadata"] = capture.Platform.Metadata.DeepClone();
        properties["deviceExecution"] = execution.Parent is null ? execution : execution.DeepClone();
        state.SetSessionCustomProperties(sessionId, properties);
    }

    private void UpdateProfile(string sessionId, JsonObject metadata, int? processId = null,
        Ansight.Pairing.Models.DeviceApplicationIconProfile? icon = null)
    {
        if (!state.TryGetSessionSnapshot(sessionId, out var session) || session!.DeviceProfileJson is null) return;
        var profile = JsonNode.Parse(session.DeviceProfileJson)!.AsObject();
        var app = profile["app"]!.AsObject();
        if (processId.HasValue) app["processId"] = processId.Value;
        if (icon is not null) app["icon"] = JsonSerializer.SerializeToNode(icon, JsonUtil.Compact);
        if (metadata["appVersion"] is { } version) app["versionName"] = version.DeepClone();
        if (metadata["buildNumber"] is { } build) app["buildNumber"] = build.DeepClone();
        if (metadata["operatingSystemVersion"] is { } os) profile["device"]!["osVersion"] = os.DeepClone();
        var json = profile.ToJsonString();
        state.SetSessionDeviceProfile(sessionId, JsonSerializer.Deserialize<Ansight.Pairing.Models.DeviceAppProfile>(json, JsonUtil.Compact), json);
    }

    private sealed class Capture(DevicePlatformProbe platform, string? expectedProcessIdentity)
    {
        public DevicePlatformProbe Platform { get; } = platform;
        public string? ExpectedProcessIdentity { get; } = expectedProcessIdentity;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Work { get; set; } = Task.CompletedTask;
        public bool Initialized { get; set; }
        public DeviceProcessSample? Previous { get; set; }
        public long PreviousTick { get; set; }
        public long LastFrame { get; set; }
        public int Segment { get; set; }
        public Dictionary<string, byte> Channels { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> LastCapabilities { get; } = new(StringComparer.Ordinal);
    }
}
