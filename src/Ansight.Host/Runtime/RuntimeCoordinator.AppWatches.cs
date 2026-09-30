using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime;

public sealed partial class RuntimeCoordinator
{
    public AppWatchService AppWatches { get; }

    public bool IsSessionLive(string sessionId)
        => AppTools.IsConnected(sessionId) || runtimeState.IsDeviceSessionActive(sessionId);

    internal AppSessionSnapshot? FindMonitoredSession(DeviceDescriptor device, string appId)
    {
        var sessionIds = AppWatches.List().Where(status => status.Watch.AppId == appId)
            .SelectMany(status => status.Devices)
            .Where(status => status.Platform == device.Platform
                && status.DeviceId.Equals(device.Identifier, StringComparison.OrdinalIgnoreCase)
                && status.State is "capturing" or "capturing-probe-error")
            .Select(status => status.SessionId).Where(id => id is not null).Distinct().ToArray();
        if (sessionIds.Length != 1 || !runtimeState.IsDeviceSessionActive(sessionIds[0]!)) return null;
        return runtimeState.TryGetSessionSnapshot(sessionIds[0]!, out var session) ? session : null;
    }

    private AppWatchService CreateAppWatches()
        => new(ApplicationPaths.ApplicationDataPath,
            new DeviceAppWatchBackend(Devices, () => UserPreferences.AdbPath, StartWatchedSessionAsync,
                physicalIosForeground: (deviceId, appId, cancellationToken) =>
                    headlessDeviceDriver?.IsPhysicalIosApplicationForegroundAsync(deviceId, appId, cancellationToken)
                    ?? Task.FromResult(false)));

    private async Task<IAppWatchCapture> StartWatchedSessionAsync(AppWatchDefinition watch,
        AppWatchObservation observation, IDisposable claim, CancellationToken cancellationToken)
    {
        var device = observation.Device!;
        if (device.Platform == DevicePlatforms.Ios && device.IsPhysical && watch.CaptureFiles.Length > 0)
            throw new InvalidOperationException("Physical iOS watches cannot capture private sandbox files; remove --capture-file.");
        if (watch.CaptureInstruments && (device.Platform != DevicePlatforms.Ios || !device.IsPhysical))
            throw new InvalidOperationException("Instruments attachment requires a physical iPhone watch.");
        var target = new WorkspaceTestTarget(device.Platform, device.Identifier, device.Name, watch.AppId, false, false, false)
        { ExecutionMode = WorkspaceExecutionModes.Device, DeviceKind = device.Kind };
        var session = await StartDeviceSessionAsync(target, claim, cancellationToken, watch,
            observation.ProcessIdentity).ConfigureAwait(false);
        return new WatchedCapture(this, watch, session);
    }

    private sealed class WatchedCapture(RuntimeCoordinator runtime, AppWatchDefinition watch,
        DeviceRunSession session) : IAppWatchCapture
    {
        public string SessionId => session.Session.SessionId;
        public bool IsActive => runtime.runtimeState.IsDeviceSessionActive(SessionId);

        public void SetAppState(global::Ansight.AppLifecycleState state, DateTimeOffset observedAtUtc)
        {
            // The foreground probe precedes capture setup. Keep the opening
            // marker within the new session, while preserving later probe times.
            var timestamp = observedAtUtc < session.Session.CreatedUtc ? session.Session.CreatedUtc : observedAtUtc;
            runtime.runtimeState.SetSessionAppState(SessionId, state, timestamp);
        }

        public void SetScreenshotInterval(int intervalMilliseconds)
        {
            runtime.externalScreenshotCaptures?.SetInterval(SessionId, intervalMilliseconds);
            if (!runtime.runtimeState.TryGetSessionSnapshot(SessionId, out var snapshot) || snapshot is null) return;
            var properties = snapshot.CustomProperties?.DeepClone().AsObject() ?? new JsonObject();
            if (properties["appWatch"] is JsonObject appWatch)
            {
                appWatch["screenshotIntervalMilliseconds"] = intervalMilliseconds;
                runtime.runtimeState.SetSessionCustomProperties(SessionId, properties);
            }
        }

        public async Task StopAsync(string reason, IReadOnlyList<string> captureFiles)
        {
            Exception? captureFailure = null;
            try
            {
                var state = runtime.runtimeState;
                HostSessionEvents.Publish(state, SessionId, reason, "host.watch." + reason, watch.Id);
                state.PublishRuntimeEvent(new RuntimeSessionCaptureEvent(DateTimeOffset.UtcNow,
                    RuntimeSessionCaptureEventKind.Stopped, SessionId, watch.AppId, session.Session.ClientName,
                    "Stopping", reason));
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var scope = ToolExecutionCancellation.Push(deadline.Token);
                foreach (var file in captureFiles)
                {
                    try
                    {
                        var result = await runtime.operationDispatcher.CallToolAsync("ansight_capture_sandbox_file",
                            new JsonObject { ["sessionId"] = SessionId, ["root"] = "data", ["path"] = file }, null).ConfigureAwait(false);
                        if (result.IsError || result.Payload?["isError"]?.GetValue<bool>() == true)
                            throw new IOException(result.ErrorMessage ?? result.Payload?["structuredContent"]?.ToJsonString() ?? "File capture failed.");
                    }
                    catch (Exception exception)
                    {
                        captureFailure = exception;
                        state.AddSessionLog(SessionId, $"App watch could not capture '{file}' on {reason}: {exception.Message}");
                    }
                }
            }
            finally { await session.DisposeAsync().ConfigureAwait(false); }
            if (captureFailure is not null) throw new IOException("One or more exit file snapshots failed; see the session logs.", captureFailure);
        }
    }
}
