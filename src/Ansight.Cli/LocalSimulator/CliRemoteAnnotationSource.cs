using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Host.Runtime.Operations.Tools.SessionEvidence;
using Ansight.RemoteSimulator.Core.Annotations;
using Ansight.RemoteSimulator.Core.Devices;
using Ansight.RemoteSimulator.Core.Recording;

namespace Ansight.Cli.LocalSimulator;

internal sealed class CliRemoteAnnotationSource : IRemoteAnnotationSource, IRemoteAnnotationFrameSource
{
    private const string AnnotationSource = "simulator.remote.mobile";
    private const string AgentActionPropertyName = "ansight.agentAction";
    private const string AnnotationBatchPropertyName = "ansight.annotationBatchId";
    private const int MaximumActiveAnnotationBatchCount = 100;
    private static readonly TimeSpan annotationBatchLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan defaultAnnotationDuration = TimeSpan.FromSeconds(2);
    private readonly RuntimeCoordinator runtime;
    private readonly Lock gate = new();
    private readonly Dictionary<string, StartedAnnotationBatch> startedBatches = new(StringComparer.Ordinal);

    public CliRemoteAnnotationSource(RuntimeCoordinator runtime)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public bool SupportsFreeDraw => true;

    public Task<IReadOnlyList<RemoteAnnotationSession>> ListLiveSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<RemoteAnnotationSession> sessions = GetLiveSessions()
            .GroupBy(
            static session => session.DeviceIdentifier,
            StringComparer.OrdinalIgnoreCase)
            .Select(static sessions => sessions
                .OrderByDescending(static session => session.Snapshot.AppState == AppLifecycleState.Foreground)
                .ThenByDescending(static session => session.Snapshot.LastUpdatedUtc)
                .First())
            .Select(static session => new RemoteAnnotationSession(
                session.DeviceIdentifier,
                session.Snapshot.SessionId,
                session.Snapshot.ClientName,
                session.Snapshot.AppId))
            .ToArray();

        return Task.FromResult(sessions);
    }

    public Task<RemoteAnnotationResult> CreateAnnotationAsync(
        RemoteAnnotationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(SaveAnnotation(request, existing: null));
    }

    public Task<RemoteAnnotationResult> UpdateAnnotationAsync(
        RemoteAnnotationUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var annotationId = NormalizeIdentifier(request.AnnotationId);

        if (annotationId is null)
        {
            return Task.FromResult(new RemoteAnnotationResult(false, "The annotation identifier is invalid."));
        }

        var session = ResolveSession(request.DeviceUdid);
        var existing = session?.Snapshot.Annotations.SingleOrDefault(annotation =>
            string.Equals(annotation.AnnotationId, annotationId, StringComparison.Ordinal));

        if (session is null || existing is null)
        {
            return Task.FromResult(new RemoteAnnotationResult(
                false,
                "The annotation is no longer available in the live Ansight session."));
        }

        var update = new RemoteAnnotationRequest(
            request.DeviceUdid,
            request.Kind,
            request.X,
            request.Y,
            request.Width,
            request.Height,
            request.Comment,
            request.AgentAction,
            request.BatchId,
            request.FrameId,
            request.Strokes);

        return Task.FromResult(SaveAnnotation(update, existing));
    }

    public Task<RemoteOperationResult> DeleteAnnotationAsync(
        RemoteAnnotationDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var annotationId = NormalizeIdentifier(request.AnnotationId);
        var batchId = NormalizeIdentifier(request.BatchId);
        var frameId = NormalizeIdentifier(request.FrameId);

        if (annotationId is null || batchId is null || frameId is null)
        {
            return Task.FromResult(new RemoteOperationResult(
                false,
                "The annotation batch delete request is invalid."));
        }

        var session = ResolveSession(request.DeviceUdid);
        var existing = session?.Snapshot.Annotations.SingleOrDefault(annotation =>
            string.Equals(annotation.AnnotationId, annotationId, StringComparison.Ordinal));

        if (session is null || existing is null)
        {
            return Task.FromResult(new RemoteOperationResult(
                false,
                "The annotation is no longer available in the live Ansight session."));
        }

        lock (gate)
        {
            PruneStartedBatches();

            if (!startedBatches.TryGetValue(batchId, out var startedBatch)
                || !string.Equals(startedBatch.SessionId, session.Snapshot.SessionId, StringComparison.Ordinal)
                || !string.Equals(startedBatch.FrameId, frameId, StringComparison.Ordinal)
                || !BelongsToBatch(existing, batchId, frameId))
            {
                return Task.FromResult(new RemoteOperationResult(
                    false,
                    "The annotation does not belong to this active batch and frame."));
            }
        }

        var result = runtime.SessionEditing.DeleteAnnotation(session.Snapshot.SessionId, annotationId);

        return Task.FromResult(new RemoteOperationResult(
            result.IsSuccess,
            result.IsSuccess ? "Annotation removed from the batch." : result.Message));
    }

    public Task<RemoteAnnotationBatchStartResult> StartAnnotationBatchAsync(
        RemoteAnnotationBatchStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var batchId = NormalizeIdentifier(request.BatchId);

        if (batchId is null)
        {
            return Task.FromResult(new RemoteAnnotationBatchStartResult(
                false,
                "The annotation batch identifier is invalid."));
        }

        var session = ResolveSession(request.DeviceUdid);

        if (session is null)
        {
            return Task.FromResult(new RemoteAnnotationBatchStartResult(
                false,
                "The selected simulator no longer has a live Ansight session."));
        }

        var frame = session.Snapshot.Images
            .OrderByDescending(static candidate => candidate.CapturedAtUtc)
            .FirstOrDefault();

        if (frame is null)
        {
            return Task.FromResult(new RemoteAnnotationBatchStartResult(
                false,
                "Wait for the live Ansight session to capture a screenshot before annotating it."));
        }

        lock (gate)
        {
            PruneStartedBatches();
            startedBatches[batchId] = new StartedAnnotationBatch(
                session.Snapshot.SessionId,
                frame.FrameId,
                frame.CapturedAtUtc.ToUniversalTime(),
                DateTimeOffset.UtcNow);
        }

        return Task.FromResult(new RemoteAnnotationBatchStartResult(
            true,
            "Annotation batch started on the current CLI host frame.",
            batchId,
            session.Snapshot.SessionId,
            frame.FrameId,
            frame.CapturedAtUtc.ToUniversalTime()));
    }

    public async Task<RemoteRecordingFrame?> GetBatchFrameAsync(
        string deviceIdentifier,
        string batchId,
        string sessionId,
        string frameId,
        CancellationToken cancellationToken = default)
    {
        var session = ResolveSession(deviceIdentifier);

        if (session is null || !string.Equals(session.Snapshot.SessionId, sessionId, StringComparison.Ordinal))
        {
            return null;
        }

        lock (gate)
        {
            PruneStartedBatches();

            var matchesBatch = startedBatches.TryGetValue(batchId, out var batch)
                && string.Equals(batch.SessionId, sessionId, StringComparison.Ordinal)
                && string.Equals(batch.FrameId, frameId, StringComparison.Ordinal);

            if (!matchesBatch)
            {
                return null;
            }
        }

        var frame = session.Snapshot.Images.FirstOrDefault(candidate => candidate.FrameId == frameId);

        if (frame is null)
        {
            return null;
        }

        var path = SessionFileLocator.ResolveScreenshotPath(runtime.ApplicationPaths, session.Snapshot, frame);

        if (!File.Exists(path))
        {
            return null;
        }

        byte[] content;

        try
        {
            await using var stream = File.OpenRead(path);

            if (stream.Length is <= 0 or > 20 * 1024 * 1024)
            {
                return null;
            }

            content = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Retained evidence may be pruned while the companion is fetching it.
            return null;
        }

        var contentType = frame.Format.ToLowerInvariant() switch
        {
            "jpg" or "jpeg" or "image/jpeg" => "image/jpeg",
            "webp" or "image/webp" => "image/webp",
            _ => "image/png"
        };

        return new RemoteRecordingFrame(contentType, content);
    }

    private RemoteAnnotationResult SaveAnnotation(
        RemoteAnnotationRequest request,
        SessionAnnotation? existing)
    {
        var comment = NormalizeComment(request.Comment);

        if (comment is null)
        {
            return new RemoteAnnotationResult(false, "Describe the issue before saving the annotation.");
        }

        var session = ResolveSession(request.DeviceUdid);

        if (session is null)
        {
            return new RemoteAnnotationResult(
                false,
                "The selected simulator no longer has a live Ansight session.");
        }

        var batchId = NormalizeIdentifier(request.BatchId);
        var requestedFrameId = NormalizeIdentifier(request.FrameId);
        var invalidBatchId = request.BatchId is not null && batchId is null;
        var invalidFrameId = request.FrameId is not null && requestedFrameId is null;

        if (invalidBatchId || invalidFrameId)
        {
            return new RemoteAnnotationResult(false, "The annotation batch or frame identifier is invalid.");
        }

        AnnotationFrameReference? frame = null;

        if (batchId is not null)
        {
            lock (gate)
            {
                PruneStartedBatches();

                var invalidBatch = !startedBatches.TryGetValue(batchId, out var startedBatch)
                    || !string.Equals(startedBatch.SessionId, session.Snapshot.SessionId, StringComparison.Ordinal)
                    || !string.Equals(startedBatch.FrameId, requestedFrameId, StringComparison.Ordinal);

                if (invalidBatch)
                {
                    return new RemoteAnnotationResult(
                        false,
                        "The annotation batch is no longer active or does not match its saved CLI host frame.");
                }

                frame = new AnnotationFrameReference(
                    startedBatch!.FrameId,
                    startedBatch.FrameCapturedAtUtc);
            }
        }
        else
        {
            var sessionFrame = requestedFrameId is null
                ? session.Snapshot.Images
                    .OrderByDescending(static candidate => candidate.CapturedAtUtc)
                    .FirstOrDefault()
                : session.Snapshot.Images.FirstOrDefault(candidate => string.Equals(
                    candidate.FrameId,
                    requestedFrameId,
                    StringComparison.Ordinal));

            if (sessionFrame is not null)
            {
                frame = new AnnotationFrameReference(
                    sessionFrame.FrameId,
                    sessionFrame.CapturedAtUtc.ToUniversalTime());
            }
        }

        if (frame is null)
        {
            return new RemoteAnnotationResult(
                false,
                requestedFrameId is null
                    ? "Wait for the live Ansight session to capture a screenshot before annotating it."
                    : "The annotation batch frame is no longer available in the live Ansight session.");
        }

        if (existing is not null && !BelongsToBatch(existing, batchId, frame.FrameId))
        {
            return new RemoteAnnotationResult(
                false,
                "The annotation does not belong to this active batch and frame.");
        }

        var shape = CliAnnotationGeometry.Create(request, frame.FrameId, frame.CapturedAtUtc, existing?.Geometry);

        if (shape.Error is not null)
        {
            return new RemoteAnnotationResult(false, shape.Error);
        }

        var kind = string.Equals(request.Kind, "freeDraw", StringComparison.OrdinalIgnoreCase)
            ? SessionAnnotationGeometryKind.FreeDraw
            : shape.Geometry[0].Kind;
        var bounds = shape.Bounds;
        var visualTreeSnapshot = session.Snapshot.VisualTreeSnapshots
            .OrderByDescending(candidate => string.Equals(
                candidate.ScreenshotFrameId,
                frame.FrameId,
                StringComparison.Ordinal))
            .ThenByDescending(static candidate => candidate.CapturedAtUtc)
            .FirstOrDefault();
        var annotationId = existing?.AnnotationId ?? Guid.NewGuid().ToString("N");
        var capturedAtUtc = frame.CapturedAtUtc;
        var annotation = new SessionAnnotation
        {
            AnnotationId = annotationId,
            StartUtc = existing?.StartUtc ?? capturedAtUtc,
            EndUtc = existing?.EndUtc ?? capturedAtUtc.Add(defaultAnnotationDuration),
            Label = BuildLabel(comment),
            Source = AnnotationSource,
            Notes = BuildNotes(comment),
            CaptureGroupId = batchId,
            CustomData = BuildCustomData(request.AgentAction, batchId),
            Evidence = existing?.Evidence ?? [],
            HookFailures = existing?.HookFailures ?? [],
            Geometry = shape.Geometry,
            Target = new SessionAnnotationTarget
            {
                Kind = "visualTreeRegion",
                Source = AnnotationSource,
                TargetId = existing?.Target?.TargetId ?? $"region-{Guid.NewGuid():N}",
                VisualTreeSnapshotId = visualTreeSnapshot?.SnapshotId ?? string.Empty,
                Type = "ScreenRegion",
                ElementKind = kind.ToString(),
                Label = kind switch
                {
                    SessionAnnotationGeometryKind.FreeDraw => "Screenshot Drawing",
                    SessionAnnotationGeometryKind.Rectangle => "Selected Screenshot Region",
                    _ => "Selected Screenshot Point"
                },
                NormalizedBounds = bounds
            }
        };

        var result = runtime.SessionEditing.UpsertAnnotation(session.Snapshot.SessionId, annotation);

        return result.IsSuccess
            ? new RemoteAnnotationResult(
                true,
                existing is null
                    ? "Annotation saved to the live CLI host session."
                    : "Annotation updated in the live CLI host session.",
                annotationId,
                session.Snapshot.SessionId,
                batchId,
                frame.FrameId,
                capturedAtUtc)
            : new RemoteAnnotationResult(false, result.Message);
    }

    private SessionSelection? ResolveSession(string deviceIdentifier)
    {
        return GetLiveSessions()
            .Where(session => string.Equals(
                session.DeviceIdentifier,
                deviceIdentifier,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static session => session.Snapshot.AppState == AppLifecycleState.Foreground)
            .ThenByDescending(static session => session.Snapshot.LastUpdatedUtc)
            .FirstOrDefault();
    }

    private IReadOnlyList<SessionSelection> GetLiveSessions()
    {
        var sessions = new List<SessionSelection>();

        foreach (var sessionId in runtime.AppTools.GetConnectedSessionIds())
        {
            if (!runtime.AppTools.IsConnected(sessionId)
                || !runtime.Sessions.TryGetLiveContentSnapshot(sessionId, out var snapshot)
                || snapshot is null)
            {
                continue;
            }

            var deviceIdentifier = ResolveRuntimeDeviceIdentifier(snapshot.LogStreams, snapshot.DeviceProfileJson);

            if (deviceIdentifier.Length > 0)
            {
                sessions.Add(new SessionSelection(deviceIdentifier, snapshot));
            }
        }

        return sessions;
    }

    private static string ResolveRuntimeDeviceIdentifier(
        IReadOnlyList<SessionLogStream> logStreams,
        string? deviceProfileJson)
    {
        foreach (var stream in logStreams)
        {
            if (stream.Metadata.TryGetValue("deviceUdid", out var deviceUdid)
                && !string.IsNullOrWhiteSpace(deviceUdid))
            {
                return deviceUdid.Trim();
            }

            if (stream.Metadata.TryGetValue("deviceSerial", out var deviceSerial)
                && !string.IsNullOrWhiteSpace(deviceSerial))
            {
                return deviceSerial.Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(deviceProfileJson))
        {
            try
            {
                using var document = JsonDocument.Parse(deviceProfileJson);

                if (document.RootElement.TryGetProperty("device", out var device)
                    && device.ValueKind == JsonValueKind.Object
                    && device.TryGetProperty("nativeDeviceId", out var nativeDeviceId)
                    && nativeDeviceId.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(nativeDeviceId.GetString()))
                {
                    return nativeDeviceId.GetString()!.Trim();
                }
            }
            catch (JsonException)
            {
                // Best effort: older and custom SDKs may supply non-standard profile JSON.
            }
        }

        return string.Empty;
    }

    private static bool BelongsToBatch(SessionAnnotation annotation, string? batchId, string frameId)
    {
        return batchId is not null
            && string.Equals(annotation.CaptureGroupId, batchId, StringComparison.Ordinal)
            && annotation.Geometry.Count > 0
            && annotation.Geometry.All(geometry => string.Equals(
                geometry.FrameId,
                frameId,
                StringComparison.Ordinal));
    }

    private static string BuildLabel(string comment)
    {
        var lineBreakIndex = comment.IndexOf('\n');

        return (lineBreakIndex < 0 ? comment : comment[..lineBreakIndex]).Trim();
    }

    private static string? BuildNotes(string comment)
    {
        var lineBreakIndex = comment.IndexOf('\n');

        return lineBreakIndex < 0
            ? null
            : NormalizeComment(comment[(lineBreakIndex + 1)..]);
    }

    private static string? NormalizeComment(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Trim();
    }

    private static string? NormalizeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();

        return normalized.Length <= 128 && normalized.All(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_')
            ? normalized
            : null;
    }

    private static JsonObject BuildCustomData(string agentAction, string? batchId)
    {
        var customData = new JsonObject
        {
            [AgentActionPropertyName] = string.Equals(
                agentAction,
                "act",
                StringComparison.OrdinalIgnoreCase)
                ? "act"
                : "investigate"
        };

        if (batchId is not null)
        {
            customData[AnnotationBatchPropertyName] = batchId;
        }

        return customData;
    }

    private void PruneStartedBatches()
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(annotationBatchLifetime);

        foreach (var batchId in startedBatches
            .Where(batch => batch.Value.StartedAtUtc < cutoff)
            .Select(static batch => batch.Key)
            .ToArray())
        {
            startedBatches.Remove(batchId);
        }

        foreach (var batchId in startedBatches
            .OrderByDescending(static batch => batch.Value.StartedAtUtc)
            .Skip(MaximumActiveAnnotationBatchCount - 1)
            .Select(static batch => batch.Key)
            .ToArray())
        {
            startedBatches.Remove(batchId);
        }
    }

    private sealed record SessionSelection(
        string DeviceIdentifier,
        AppSessionSnapshot Snapshot);

    private sealed record AnnotationFrameReference(
        string FrameId,
        DateTimeOffset CapturedAtUtc);

    private sealed record StartedAnnotationBatch(
        string SessionId,
        string FrameId,
        DateTimeOffset FrameCapturedAtUtc,
        DateTimeOffset StartedAtUtc);
}
