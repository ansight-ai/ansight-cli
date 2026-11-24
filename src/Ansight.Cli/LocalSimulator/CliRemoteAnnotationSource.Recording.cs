using System.Text.Json.Nodes;
using Ansight.Host.Models.Session;
using Ansight.Host.Transcription;
using Ansight.RemoteSimulator.Core.Annotations;
namespace Ansight.Cli.LocalSimulator;

internal sealed partial class CliRemoteAnnotationSource
{
    private RemoteAnnotationResult SaveTranscript(RemoteAnnotationRequest request)
    {
        var batchId = NormalizeIdentifier(request.BatchId);
        if (!Guid.TryParse(batchId, out var id))
        {
            return new(false, UnavailableBatchMessage);
        }

        var batch = FindStartedBatch(request.DeviceUdid, batchId!, request.FrameId);
        if (batch is null)
        {
            return new(false, UnavailableBatchMessage);
        }

        if (request.Kind == "transcript-complete")
        {
            var completion = SessionRecordingTranscript.Complete(runtime, batch.SessionId, id);
            return new(completion.IsSuccess, completion.Message, SessionId: batch.SessionId);
        }

        var itemId = NormalizeIdentifier(request.RecordingItemId);
        var start = request.RecordingStartUtc;
        var end = request.RecordingEndUtc;
        var hasValidTiming = start.HasValue
            && end.HasValue
            && start.Value >= batch.StartedAtUtc.AddSeconds(-5);
        if (itemId is null || !hasValidTiming)
        {
            return new(false, "The transcript segment or its timestamp is invalid.");
        }

        var segment = new SessionTranscriptSegment(itemId, start!.Value, end!.Value, request.Comment);
        var result = SessionRecordingTranscript.SaveSegment(runtime, batch.SessionId, id, "Companion microphone", segment);
        return new(result.IsSuccess, result.Message, itemId, batch.SessionId, batchId, batch.FrameId);
    }

    private RemoteAnnotationResult SaveRecordingItem(RemoteAnnotationRequest request)
    {
        var batchId = NormalizeIdentifier(request.BatchId);
        var itemId = NormalizeIdentifier(request.RecordingItemId);
        var batch = batchId is null ? null : FindStartedBatch(request.DeviceUdid, batchId, request.FrameId);
        var comment = NormalizeComment(request.Comment);
        if (batch is null)
        {
            return new(false, UnavailableBatchMessage);
        }

        if (itemId is null || comment is null)
        {
            return new(false, "The recording item is invalid. Restart recording with the current host.");
        }

        if (request.RecordingStartUtc is not { } start || request.RecordingEndUtc is not { } end)
        {
            return new(false, "The recording item is missing timestamps.");
        }

        var validRange = end >= start
            && end - start <= TimeSpan.FromMinutes(2)
            && start >= batch.StartedAtUtc.AddSeconds(-5)
            && end <= DateTimeOffset.UtcNow.AddSeconds(5);
        if (!validRange || request.Kind is not ("speech" or "freeDraw"))
        {
            return new(false, "The recording item or its timestamp is invalid. Restart recording with the current host.");
        }

        if (!runtime.Sessions.TryGetSnapshot(batch.SessionId, out var session) || session is null)
        {
            return new(false, UnavailableBatchMessage);
        }

        var id = $"recording-{batchId}-{itemId}";
        var existing = session.Annotations.FirstOrDefault(a => a.AnnotationId == id);
        if (existing is not null)
        {
            return new(true, "Recording item already saved.", id, session.SessionId, batchId, batch.FrameId, start);
        }

        var frame = session.Images.MinBy(f => Math.Abs((f.CapturedAtUtc - start).TotalMilliseconds));
        if (frame is null)
        {
            return new(false, UnavailableBatchMessage);
        }

        var geometry = request.Kind == "speech" ? null
            : CliAnnotationGeometry.Create(request, frame.FrameId, frame.CapturedAtUtc, null);
        if (geometry?.Error is { } error)
        {
            return new(false, error);
        }

        var annotation = new SessionAnnotation
        {
            AnnotationId = id,
            StartUtc = start,
            EndUtc = end,
            Label = BuildLabel(comment),
            Notes = BuildNotes(comment),
            Source = "companion.exploration",
            CaptureGroupId = batchId,
            CustomData = new JsonObject
            {
                ["kind"] = request.Kind == "speech" ? "speech" : "marking",
                [AnnotationBatchPropertyName] = batchId,
                [AgentActionPropertyName] = "investigate"
            },
            Geometry = geometry?.Geometry ?? []
        };
        var result = runtime.SessionEditing.UpsertAnnotation(session.SessionId, annotation);
        return new(result.IsSuccess, result.Message, id, session.SessionId, batchId, frame.FrameId, start);
    }
}
