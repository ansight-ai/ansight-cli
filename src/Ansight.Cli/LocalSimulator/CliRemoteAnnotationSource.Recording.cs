using System.Text.Json.Nodes;
using Ansight.RemoteSimulator.Core.Annotations;
namespace Ansight.Cli.LocalSimulator;

internal sealed partial class CliRemoteAnnotationSource
{
    private RemoteAnnotationResult SaveRecordingItem(RemoteAnnotationRequest request)
    {
        var batchId = NormalizeIdentifier(request.BatchId);
        var itemId = NormalizeIdentifier(request.RecordingItemId);
        var batch = batchId is null ? null : FindStartedBatch(request.DeviceUdid, batchId, request.FrameId);
        var comment = NormalizeComment(request.Comment);
        if (batch is null || itemId is null || comment is null
            || request.RecordingStartUtc is not { } start || request.RecordingEndUtc is not { } end
            || end < start || end - start > TimeSpan.FromMinutes(2)
            || start < batch.StartedAtUtc.AddSeconds(-5) || end > DateTimeOffset.UtcNow.AddSeconds(5)
            || request.Kind is not ("speech" or "freeDraw"))
            return new(false, "The recording item or its timestamp is invalid. Restart recording with the current host.");
        if (!runtime.Sessions.TryGetSnapshot(batch.SessionId, out var session) || session is null)
            return new(false, UnavailableBatchMessage);
        var id = $"recording-{batchId}-{itemId}";
        var existing = session.Annotations.FirstOrDefault(a => a.AnnotationId == id);
        if (existing is not null)
            return new(true, "Recording item already saved.", id, session.SessionId, batchId, batch.FrameId, start);
        var frame = session.Images.MinBy(f => Math.Abs((f.CapturedAtUtc - start).TotalMilliseconds));
        if (frame is null) return new(false, UnavailableBatchMessage);
        var geometry = request.Kind == "speech" ? null
            : CliAnnotationGeometry.Create(request, frame.FrameId, frame.CapturedAtUtc, null);
        if (geometry?.Error is { } error) return new(false, error);
        var annotation = new SessionAnnotation
        {
            AnnotationId = id, StartUtc = start, EndUtc = end,
            Label = BuildLabel(comment), Notes = BuildNotes(comment),
            Source = "companion.exploration", CaptureGroupId = batchId,
            CustomData = new JsonObject { ["kind"] = request.Kind == "speech" ? "speech" : "marking",
                [AnnotationBatchPropertyName] = batchId, [AgentActionPropertyName] = "investigate" },
            Geometry = geometry?.Geometry ?? [], Evidence = [], HookFailures = []
        };
        var result = runtime.SessionEditing.UpsertAnnotation(session.SessionId, annotation);
        return new(result.IsSuccess, result.Message, id, session.SessionId, batchId, frame.FrameId, start);
    }
}
