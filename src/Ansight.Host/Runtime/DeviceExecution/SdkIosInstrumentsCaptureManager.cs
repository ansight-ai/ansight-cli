using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Devices;
using Ansight.Pairing.Models;
using Ansight.SimCtl;
using Ansight.Infrastructure.Preferences;

namespace Ansight.Host.Runtime.DeviceExecution;

/// <summary>Best-effort Instruments capture for an iOS app connected through the SDK.</summary>
[Export(typeof(SdkIosInstrumentsCaptureManager))]
[PartCreationPolicy(CreationPolicy.Shared)]
internal sealed class SdkIosInstrumentsCaptureManager
{
    private static readonly Ansight.Infrastructure.Logging.ILogger log = Ansight.Infrastructure.Logging.Logger.Create();
    private readonly IRuntimeState state;
    private readonly IUserPreferences preferences;
    private readonly IApplicationPaths paths;
    private readonly Lock gate = new();
    private readonly Dictionary<string, CaptureHandle> captures = new(StringComparer.Ordinal);

    [ImportingConstructor]
    public SdkIosInstrumentsCaptureManager(IRuntimeState state, IUserPreferences preferences, IApplicationPaths paths)
    {
        this.state = state;
        this.preferences = preferences;
        this.paths = paths;
    }

    public void Attach(string sessionId, DeviceAppProfile? profile, string? profileJson)
    {
        if (profile is null || !IsIos(profile)) return;
        lock (gate)
        {
            if (captures.ContainsKey(sessionId)) return;
        }
        if (!OperatingSystem.IsMacOS())
        {
            MarkUnavailable(sessionId, "iOS Instruments capture requires Xcode on macOS.");
            return;
        }
        if (profile.App?.ProcessId is not > 0)
        {
            MarkUnavailable(sessionId, "The SDK profile did not report an app process ID.");
            return;
        }

        lock (gate)
        {
            if (captures.ContainsKey(sessionId)) return;
            var handle = new CaptureHandle();
            captures.Add(sessionId, handle);
            handle.StartTask = Task.Run(() => StartAsync(sessionId, profile, profileJson, handle.Cancellation.Token));
        }
    }

    public async Task StopAsync(string sessionId)
    {
        CaptureHandle? handle;
        lock (gate)
        {
            if (!captures.Remove(sessionId, out handle)) return;
        }

        await handle.Cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            var capture = await handle.StartTask.ConfigureAwait(false);
            if (capture is not null) await capture.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            handle.Cancellation.Dispose();
        }
    }

    private async Task<PhysicalIosInstrumentsCapture?> StartAsync(
        string sessionId, DeviceAppProfile profile, string? profileJson, CancellationToken cancellationToken)
    {
        try
        {
            var target = await ResolveTargetAsync(profile, profileJson, cancellationToken).ConfigureAwait(false);
            if (target.DeviceId is null)
            {
                MarkUnavailable(sessionId, target.Reason ?? "No matching iOS device was found.");
                return null;
            }

            return await PhysicalIosInstrumentsCapture.StartAsync(sessionId, target.DeviceId,
                profile.App!.ProcessId!.Value, paths.ApplicationTempPath, state,
                profile.Device?.IsVirtual == true || profile.Device?.IsEmulator == true
                    ? PhysicalIosInstrumentsCapture.TimeProfilerTemplate
                    : PhysicalIosInstrumentsCapture.ActivityMonitorTemplate,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            MarkUnavailable(sessionId, exception.Message);
            return null;
        }
    }

    private async Task<TargetResolution> ResolveTargetAsync(
        DeviceAppProfile profile, string? profileJson, CancellationToken cancellationToken)
    {
        var tool = await SimCtlToolLocator.ResolveAsync(preferences.XcodePath, cancellationToken).ConfigureAwait(false);
        if (!tool.IsFound) return new TargetResolution(null, tool.Message);
        var processId = profile.App!.ProcessId!.Value;
        var nativeDeviceId = ReadNativeDeviceId(profileJson);
        if (profile.Device?.IsVirtual == true || profile.Device?.IsEmulator == true)
        {
            var client = new SimCtlClient(tool);
            var booted = (await client.GetDevicesAsync(cancellationToken).ConfigureAwait(false))
                .Where(device => device.IsBooted
                    && (nativeDeviceId is null || string.Equals(device.Udid, nativeDeviceId,
                        StringComparison.OrdinalIgnoreCase))).ToArray();
            var matches = new List<SimCtlDevice>();
            foreach (var device in booted)
                if (await client.ProcessExistsAsync(device.Udid, processId, cancellationToken).ConfigureAwait(false))
                    matches.Add(device);
            return matches.Count == 1
                ? new TargetResolution(matches[0].Udid, null)
                : new TargetResolution(null, matches.Count == 0
                    ? "No booted simulator matched the SDK app process."
                    : "More than one simulator matched the SDK app process.");
        }

        var bundleId = profile.App?.AppId?.Trim();
        if (string.IsNullOrWhiteSpace(bundleId))
            return new TargetResolution(null, "The SDK profile did not report an app bundle ID.");
        var coreDevice = new CoreDeviceClient(tool);
        var devices = (await coreDevice.GetDevicesAsync(cancellationToken).ConfigureAwait(false))
            .Where(device => device.IsIos && device.IsAvailable
                && (nativeDeviceId is null || string.Equals(device.Identifier, nativeDeviceId,
                    StringComparison.OrdinalIgnoreCase))).ToArray();
        if (nativeDeviceId is not null)
            return devices.Length == 1
                ? new TargetResolution(devices[0].Identifier, null)
                : new TargetResolution(null, "The SDK's iPhone is not connected with Developer Mode enabled.");
        var matchingDevices = new List<string>();
        foreach (var device in devices)
        {
            try
            {
                var identity = await coreDevice.GetRunningApplicationProcessIdentityAsync(
                    device.Identifier, bundleId, cancellationToken).ConfigureAwait(false);
                if (PhysicalIosInstrumentsCapture.TryParseProcessId(identity, out var actualProcessId)
                    && actualProcessId == processId)
                    matchingDevices.Add(device.Identifier);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                log.Warning($"sdk_instruments_process_probe_failed deviceId={device.Identifier} reason=\"{exception.Message}\"");
            }
        }

        return matchingDevices.Count == 1
            ? new TargetResolution(matchingDevices[0], null)
            : new TargetResolution(null, matchingDevices.Count == 0
                ? "No developer-enabled iPhone matched the SDK app process."
                : "More than one iPhone matched the SDK app process.");
    }

    internal static bool IsIos(DeviceAppProfile profile)
        => string.Equals(profile.Device?.OsName?.Trim(), "ios", StringComparison.OrdinalIgnoreCase);

    internal static string? ReadNativeDeviceId(string? profileJson)
    {
        if (string.IsNullOrWhiteSpace(profileJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(profileJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("device", out var device)
                || device.ValueKind != JsonValueKind.Object) return null;
            return device.TryGetProperty("nativeDeviceId", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()?.Trim() is { Length: > 0 } value ? value : null
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void MarkUnavailable(string sessionId, string reason)
    {
        if (!state.TryGetSessionSnapshot(sessionId, out var session)) return;
        var properties = session!.CustomProperties?.DeepClone().AsObject() ?? new JsonObject();
        properties["instruments"] = new JsonObject
        {
            ["status"] = "unavailable",
            ["reason"] = reason,
            ["checkedUtc"] = DateTimeOffset.UtcNow
        };
        state.SetSessionCustomProperties(sessionId, properties);
        HostSessionEvents.Publish(state, sessionId,
            "instruments.unavailable", "host.instruments.unavailable", reason);
    }

    private sealed record TargetResolution(string? DeviceId, string? Reason);

    private sealed class CaptureHandle
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public Task<PhysicalIosInstrumentsCapture?> StartTask { get; set; }
            = Task.FromResult<PhysicalIosInstrumentsCapture?>(null);
    }
}
