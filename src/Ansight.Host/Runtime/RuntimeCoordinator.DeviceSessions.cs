using System.Text.Json.Nodes;
using Ansight.Host.Runtime.DeviceExecution;
using Ansight.Host.Runtime.Operations.Tools.SessionEvidence;
using System.Globalization;

namespace Ansight.Host.Runtime;

public sealed partial class RuntimeCoordinator
{
    private async Task BackfillIosInstrumentsMetricsAsync(CancellationToken cancellationToken)
    {
        foreach (var summary in runtimeState.GetSessionSummaries())
        {
            var instruments = summary.CustomProperties?["instruments"];
            if (instruments?["status"]?.ToString() != "ingested"
                || instruments["sampleMetricsIngested"]?.ToString() == "true")
                continue;
            try
            {
                if (!runtimeState.TryGetSessionSnapshot(summary.SessionId, out var session)
                    || session is null) continue;
                var snapshotId = instruments["snapshotId"]?.ToString();
                var artifact = session.ArtifactSnapshots.FirstOrDefault(item =>
                    item.SnapshotId == snapshotId && item.Source == "instruments");
                if (artifact is null)
                    throw new IOException("The retained Instruments artifact is missing.");
                var processIdentity = instruments["processId"]?.ToString()
                    ?? session.CustomProperties?["appWatch"]?["processIdentity"]?.ToString();
                var hasProcessId = PhysicalIosInstrumentsCapture.TryParseProcessId(
                    processIdentity, out var processId);
                if (!hasProcessId && session.DeviceProfile?.App?.ProcessId is > 0)
                {
                    processId = session.DeviceProfile.App.ProcessId.Value;
                    hasProcessId = true;
                }
                if (!DateTimeOffset.TryParse(instruments["startedUtc"]?.ToString(),
                        CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var startedUtc)
                    || !hasProcessId)
                    throw new InvalidDataException("The Instruments capture lacks its start time or process ID.");

                var xmlEntry = artifact.Entries.FirstOrDefault(item =>
                    item.ArchiveRelativePath == "activity-monitor-process.xml");
                PhysicalIosInstrumentsMetricResult result;
                if (xmlEntry is not null && SessionFileLocator.TryResolveArtifactEntryPath(
                        ApplicationPaths, session, artifact, xmlEntry, out var xmlPath) && File.Exists(xmlPath))
                {
                    try
                    {
                        result = PhysicalIosInstrumentsMetrics.Ingest(runtimeState, session.SessionId,
                            xmlPath, startedUtc, processId);
                    }
                    catch (Exception exception) when ((exception is InvalidDataException or System.Xml.XmlException)
                        && instruments["template"]?.ToString() == PhysicalIosInstrumentsCapture.ActivityMonitorTemplate)
                    {
                        result = await RecoverFromTraceAsync().ConfigureAwait(false);
                    }
                }
                else
                {
                    if (instruments["template"]?.ToString() != PhysicalIosInstrumentsCapture.ActivityMonitorTemplate)
                        continue;
                    result = await RecoverFromTraceAsync().ConfigureAwait(false);
                }
                PhysicalIosInstrumentsMetrics.MarkIngested(runtimeState, session.SessionId, result);
                SetInstrumentsRecoveryStatus(session.SessionId, "succeeded", null);

                async Task<PhysicalIosInstrumentsMetricResult> RecoverFromTraceAsync()
                {
                    var traceEntry = artifact.Entries.FirstOrDefault(item =>
                        item.ArchiveRelativePath == "capture.trace.zip");
                    if (traceEntry is null || !SessionFileLocator.TryResolveArtifactEntryPath(
                            ApplicationPaths, session, artifact, traceEntry, out var archivePath)
                        || !File.Exists(archivePath))
                        throw new IOException("The retained Instruments trace archive is missing.");
                    return await PhysicalIosInstrumentsTraceRecovery.IngestAsync(runtimeState,
                        session.SessionId, archivePath, ApplicationPaths.ApplicationTempPath,
                        startedUtc, processId, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException
                                               and not OutOfMemoryException)
            {
                SetInstrumentsRecoveryStatus(summary.SessionId, "failed", exception.Message);
                log.Warning($"instruments_metrics_backfill_failed sessionId={summary.SessionId} reason=\"{exception.Message}\"");
            }
        }
    }

    private void SetInstrumentsRecoveryStatus(string sessionId, string status, string? error)
    {
        if (!runtimeState.TryGetSessionSnapshot(sessionId, out var session) || session is null) return;
        var properties = session.CustomProperties?.DeepClone().AsObject() ?? new JsonObject();
        if (properties["instruments"] is not JsonObject instruments) return;
        instruments["metricsRecovery"] = new JsonObject
        {
            ["status"] = status,
            ["attemptedUtc"] = DateTimeOffset.UtcNow,
            ["error"] = error
        };
        runtimeState.SetSessionCustomProperties(sessionId, properties);
    }

    internal async Task<DeviceRunSession> StartDeviceSessionAsync(
        WorkspaceTestTarget target, IDisposable? deviceClaim, CancellationToken cancellationToken,
        AppWatchDefinition? watch = null, string? processIdentity = null)
    {
        var captures = externalScreenshotCaptures
            ?? throw new InvalidOperationException("Device execution requires host screenshot capture.");
        var tail = TimeSpan.Zero;
        if (Apps.TryGetDefinition(target.ApplicationIdentifier, out var app) && !string.IsNullOrWhiteSpace(app?.CodebasePath))
            tail = WorkspaceTrendsCatalog.Load(app.CodebasePath).Definitions
                .Where(definition => definition.Enabled && definition.AppId == target.ApplicationIdentifier)
                .Select(definition => definition.ObservationTail).DefaultIfEmpty(TimeSpan.Zero).Max();
        var sessionId = runtimeState.CreateDeviceSession(target);
        var physicalIos = target.Platform == DevicePlatforms.Ios && DeviceKinds.IsPhysical(target.DeviceKind);
        if (watch is not null)
        {
            runtimeState.SetSessionCustomProperties(sessionId, new JsonObject
            {
                ["appWatch"] = new JsonObject
                {
                    ["id"] = watch.Id, ["processIdentity"] = processIdentity,
                    ["detectedAtUtc"] = DateTimeOffset.UtcNow, ["deviceSelector"] = watch.DeviceId,
                    ["screenshotIntervalMilliseconds"] = watch.ScreenshotIntervalMilliseconds
                }
            });
            var name = Apps.Get(target.ApplicationIdentifier)?.Name ?? target.ApplicationIdentifier;
            runtimeState.UpdateSessionMetadata(sessionId, false, ["app-watch"], null,
                $"{name} — {target.DeviceName} (automatic)");
        }
        runtimeState.TryGetSessionSnapshot(sessionId, out var session);
        Func<Task>? releaseResources = physicalIos && headlessDeviceDriver is not null
            ? () => headlessDeviceDriver.ReleasePhysicalIosMonitoringAsync(target.DeviceIdentifier,
                target.ApplicationIdentifier, CancellationToken.None)
            : null;
        PhysicalIosInstrumentsCapture? instruments = null;
        var lease = new DeviceRunSession(session!, runtimeState, captures, deviceClaim,
            physicalIos ? null : deviceEvidence, tail,
            RepositoryAutomations.DrainSessionAsync, releaseResources,
            () => instruments?.StopAsync() ?? Task.CompletedTask);
        try
        {
            if (physicalIos)
            {
                if (headlessDeviceDriver is null)
                    throw new InvalidOperationException("Physical iOS monitoring requires the Appium device driver.");
                await headlessDeviceDriver.PreparePhysicalIosMonitoringAsync(target.DeviceIdentifier,
                    target.ApplicationIdentifier, cancellationToken).ConfigureAwait(false);
                var properties = session!.CustomProperties?.DeepClone().AsObject() ?? new JsonObject();
                const string unavailable = "Physical iOS does not expose this through external app monitoring.";
                var capabilities = new JsonObject();
                foreach (var name in new[] { "files.read", "files.capture", "telemetry.process.cpu",
                             "telemetry.process.memory", "telemetry.fps" })
                    capabilities[name] = new JsonObject
                    {
                        ["available"] = false, ["provider"] = "appium-xcuitest", ["reason"] = unavailable
                    };
                properties["deviceExecution"] = new JsonObject
                {
                    ["collectorVersion"] = "external-physical-ios-v1", ["capabilities"] = capabilities
                };
                runtimeState.SetSessionCustomProperties(sessionId, properties);
                runtimeState.AddSessionLog(sessionId,
                    "Physical iOS external capture uses Appium UI evidence. Process metrics and private sandbox files are unavailable without an app integration.");
            }
            var policy = await captures.AttachAsync(sessionId, session!.DeviceProfile, session.DeviceProfileJson,
                new ExternalSessionScreenshotCaptureRequest(1024,
                    watch?.ScreenshotIntervalMilliseconds ?? AppWatchDefinition.DefaultScreenshotIntervalMilliseconds),
                cancellationToken).ConfigureAwait(false);
            if (policy.Mode != ExternalSessionScreenshotCapturePolicy.HostMode)
                throw new InvalidOperationException($"Device screenshot capture is unavailable: {policy.Reason}");
            if (deviceEvidence is not null && !physicalIos)
                await deviceEvidence.AttachAsync(sessionId, target, cancellationToken, processIdentity).ConfigureAwait(false);
            if (physicalIos && watch?.CaptureInstruments == true)
            {
                try
                {
                    instruments = await PhysicalIosInstrumentsCapture.StartAsync(sessionId, target.DeviceIdentifier,
                        processIdentity ?? string.Empty, ApplicationPaths.ApplicationTempPath, runtimeState)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    if (runtimeState.TryGetSessionSnapshot(sessionId, out var failedSession))
                    {
                        var properties = failedSession!.CustomProperties?.DeepClone().AsObject() ?? new JsonObject();
                        properties["instruments"] = new JsonObject { ["status"] = "failed", ["error"] = exception.Message };
                        runtimeState.SetSessionCustomProperties(sessionId, properties);
                    }
                    HostSessionEvents.Publish(runtimeState, sessionId,
                        "instruments.attach.failed", "host.instruments.attach.failed", exception.Message);
                }
            }
            runtimeState.PublishRuntimeEvent(new RuntimeSessionCaptureEvent(DateTimeOffset.UtcNow,
                RuntimeSessionCaptureEventKind.Started, sessionId, session.AppId, session.ClientName, "Capturing", "Host device capture started."));
            HostSessionEvents.Publish(runtimeState, sessionId, "run.started", "host.run.started");
            if (watch is not null) HostSessionEvents.Publish(runtimeState, sessionId,
                "process.observed", "host.watch.process.observed", watch.Id);
            return lease;
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
