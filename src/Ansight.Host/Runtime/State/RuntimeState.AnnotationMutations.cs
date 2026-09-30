using System.Text.Json.Nodes;
using static Ansight.Host.Runtime.State.RuntimeSnapshotNormalizer;

namespace Ansight.Host.Runtime.State;

internal sealed partial class RuntimeState
{
    // Leave headroom below the task bridge limit. Check the exact response before committing a write.
    private const int MaximumAnnotationMutationResultCharacters = 480_000;

    public SessionAnnotationMutationResult MutateSessionAnnotation(string sessionId, SessionAnnotationMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (string.IsNullOrWhiteSpace(sessionId) || !EnsureSessionLoaded(sessionId.Trim()))
        {
            return SessionAnnotationMutationResult.Failure("The selected session was not found.");
        }

        var selectedSessionId = sessionId.Trim();
        SessionAnnotationMutationResult result;
        var changed = false;
        lock (gate)
        {
            if (!sessionsById.TryGetValue(selectedSessionId, out var session))
            {
                return SessionAnnotationMutationResult.Failure("The selected session was not found.");
            }

            var annotationId = mutation.AnnotationId ?? Guid.NewGuid().ToString("N");
            var existingIndex = session.Annotations.FindIndex(annotation =>
                string.Equals(annotation.AnnotationId, annotationId, StringComparison.Ordinal));
            var existing = existingIndex < 0 ? null : session.Annotations[existingIndex];
            if (mutation.Kind == SessionAnnotationMutationKind.Create && existing is not null)
            {
                return SessionAnnotationMutationResult.Failure($"Annotation '{annotationId}' already exists in this session.");
            }
            if (mutation.Kind != SessionAnnotationMutationKind.Create && existing is null)
            {
                return SessionAnnotationMutationResult.Failure($"Annotation '{annotationId}' was not found in this session.");
            }
            if (mutation.ExpectedSource is not null && !string.Equals(mutation.ExpectedSource, existing?.Source, StringComparison.Ordinal))
            {
                return SessionAnnotationMutationResult.Failure($"Annotation '{annotationId}' source does not match expectedSource.");
            }

            var isRemoval = mutation.Kind == SessionAnnotationMutationKind.Remove;
            SessionAnnotation annotation;
            if (isRemoval)
            {
                annotation = existing!;
            }
            else if (!TryApplyAnnotationMutation(session, mutation, annotationId, existing, out var updated, out var error))
            {
                return SessionAnnotationMutationResult.Failure(error!);
            }
            else
            {
                annotation = updated!;
            }

            var payload = new JsonObject
            {
                ["sessionId"] = session.SessionId,
                ["appId"] = session.AppId,
                ["annotationId"] = annotation.AnnotationId,
                [isRemoval ? "deletedAnnotation" : "annotation"] = PayloadJson.BuildSessionAnnotationPayload(annotation)
            };
            if (payload.ToJsonString(JsonUtil.Compact).Length > MaximumAnnotationMutationResultCharacters)
            {
                return SessionAnnotationMutationResult.Failure("The annotation result exceeds the task response limit. No changes were applied.");
            }

            result = SessionAnnotationMutationResult.Success(payload);
            if (isRemoval)
            {
                session.Annotations.RemoveAt(existingIndex);
                changed = true;
            }
            else if (existing is null)
            {
                session.Annotations.Add(annotation);
                changed = true;
            }
            else if (!SessionAnnotationsEqual(existing, annotation))
            {
                session.Annotations[existingIndex] = annotation;
                changed = true;
            }
            if (changed)
            {
                session.Annotations.Sort(CompareAnnotations);
                session.MarkAnnotationsChanged();
            }
        }

        if (changed)
        {
            PersistAndBroadcast(selectedSessionId);
            log.Info($"session_annotation_mutated sessionId={selectedSessionId} annotationId={result.Payload!["annotationId"]} operation={mutation.Kind}");
        }
        return result;
    }

    private static bool TryApplyAnnotationMutation(
        SessionState session,
        SessionAnnotationMutation mutation,
        string annotationId,
        SessionAnnotation? existing,
        out SessionAnnotation? annotation,
        out string? error)
    {
        annotation = null;
        error = null;
        var label = mutation.Label ?? existing?.Label;
        if (string.IsNullOrWhiteSpace(label))
        {
            error = "Annotation label must not be empty.";
            return false;
        }

        var defaultStartUtc = session.LastUpdatedUtc > DateTimeOffset.MinValue ? session.LastUpdatedUtc : DateTimeOffset.UtcNow;
        var startUtc = (mutation.StartUtc ?? existing?.StartUtc ?? defaultStartUtc).ToUniversalTime();
        var endUtc = (mutation.HasEndUtc ? mutation.EndUtc : existing?.EndUtc)?.ToUniversalTime();
        if (endUtc < startUtc)
        {
            error = "endUtc must not precede startUtc.";
            return false;
        }
        if (endUtc == startUtc)
        {
            endUtc = null;
        }

        IReadOnlyList<SessionAnnotationGeometry> geometries;
        if (mutation.Geometries is null)
        {
            geometries = existing?.Geometry.Select(SessionSnapshotCloner.CloneAnnotationGeometry).ToArray() ?? [];
        }
        else if (!AnnotationMutationEvidenceResolver.TryResolveGeometries(session, mutation.Geometries, out geometries, out error))
        {
            return false;
        }

        var source = existing?.Source ?? mutation.Source ?? OperationDefaults.DefaultInjectedAnnotationSource;
        var target = SessionSnapshotCloner.CloneAnnotationTarget(existing?.Target);
        if (mutation.HasTarget)
        {
            if (mutation.Target is null)
            {
                target = null;
            }
            else if (!AnnotationMutationEvidenceResolver.TryResolveTarget(session, mutation.Target, source, out target, out error))
            {
                return false;
            }
        }

        annotation = new SessionAnnotation
        {
            AnnotationId = annotationId,
            StartUtc = startUtc,
            EndUtc = endUtc,
            Label = label,
            Source = source,
            Notes = mutation.HasNotes ? mutation.Notes : existing?.Notes,
            CaptureGroupId = existing?.CaptureGroupId,
            CustomData = existing?.CustomData?.DeepClone() as JsonObject,
            Evidence = existing?.Evidence.Select(SessionSnapshotCloner.CloneAnnotationEvidence).ToArray() ?? [],
            HookFailures = existing?.HookFailures.ToArray() ?? [],
            Geometry = geometries,
            Target = target
        };
        return true;
    }
}
