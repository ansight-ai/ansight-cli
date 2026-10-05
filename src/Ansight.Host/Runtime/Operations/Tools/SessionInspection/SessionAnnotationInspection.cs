using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal static class SessionAnnotationInspection
{
    public static RequestResult BuildGetAnnotationsResult(SessionResolver sessionResolver, JsonObject? arguments)
    {
        if (!sessionResolver.TryResolveSession(arguments, requireLiveSession: false, out var snapshot, out var resolutionError))
        {
            return ToolError(resolutionError);
        }

        if (!ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "startUtc", out var startUtc, out var errorMessage)
            || !ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "endUtc", out var endUtc, out errorMessage)
            || !ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "targetUtc", out var targetUtc, out errorMessage)
            || !ArgumentReader.TryReadPositiveLimit(arguments, "limit", OperationDefaults.DefaultAnnotationResultLimit, OperationDefaults.MaxAnnotationResultLimit, out var limit, out errorMessage)
            || !ArgumentReader.TryReadOptionalBooleanArgument(arguments, "hasStatus", out var hasStatus, out errorMessage)
            || !ArgumentReader.TryReadOptionalBooleanArgument(arguments, "hasGeometry", out var hasGeometry, out errorMessage))
        {
            return ToolError(errorMessage ?? "Invalid annotation filters.");
        }

        var labelQuery = arguments?["labelQuery"]?.GetValue<string>()?.Trim();
        var annotationStatus = arguments?["annotationStatus"]?.GetValue<string>()?.Trim();
        var frameId = arguments?["frameId"]?.GetValue<string>()?.Trim();
        var matchedAnnotations = snapshot!.Annotations
            .Where(annotation => MatchesAnnotationTimeRange(annotation, startUtc, endUtc))
            .Where(annotation => !targetUtc.HasValue || MatchesAnnotationPoint(annotation, targetUtc.Value))
            .Where(annotation => string.IsNullOrWhiteSpace(labelQuery) || MatchesAnnotationLabel(annotation, labelQuery))
            .Where(annotation => string.IsNullOrWhiteSpace(annotationStatus) || string.Equals(annotation.Status, annotationStatus, StringComparison.OrdinalIgnoreCase))
            .Where(annotation => !hasStatus.HasValue || (!string.IsNullOrWhiteSpace(annotation.Status)) == hasStatus.Value)
            .Where(annotation => string.IsNullOrWhiteSpace(frameId) || annotation.Geometry.Any(geometry => string.Equals(geometry.FrameId, frameId, StringComparison.Ordinal)))
            .Where(annotation => !hasGeometry.HasValue || (annotation.Geometry.Count > 0) == hasGeometry.Value)
            .OrderBy(annotation => annotation.StartUtc)
            .ThenBy(annotation => annotation.EndUtc ?? annotation.StartUtc)
            .ThenBy(annotation => annotation.AnnotationId, StringComparer.Ordinal)
            .ToArray();

        var returnedAnnotations = matchedAnnotations.Length > limit
            ? matchedAnnotations.Take(limit).ToArray()
            : matchedAnnotations;

        return RequestResult.ToolResult(
            new JsonObject
            {
                ["sessionId"] = snapshot.SessionId,
                ["appId"] = snapshot.AppId,
                ["clientName"] = snapshot.ClientName,
                ["status"] = snapshot.Status,
                ["filters"] = new JsonObject
                {
                    ["startUtc"] = startUtc,
                    ["endUtc"] = endUtc,
                    ["targetUtc"] = targetUtc,
                    ["labelQuery"] = labelQuery,
                    ["annotationStatus"] = annotationStatus,
                    ["hasStatus"] = hasStatus,
                    ["frameId"] = frameId,
                    ["hasGeometry"] = hasGeometry
                },
                ["matchedAnnotationCount"] = matchedAnnotations.Length,
                ["returnedAnnotationCount"] = returnedAnnotations.Length,
                ["isTruncated"] = matchedAnnotations.Length > returnedAnnotations.Length,
                ["annotations"] = PayloadJson.CreateJsonArray(returnedAnnotations.Select(annotation => (JsonNode?)PayloadJson.BuildSessionAnnotationPayload(annotation)))
            },
            isError: false);
    }

    public static RequestResult BuildInjectAnnotationResult(
        IRuntimeState runtimeState,
        SessionResolver sessionResolver,
        JsonObject? arguments)
    {
        if (!sessionResolver.TryResolveSession(arguments, requireLiveSession: false, out var snapshot, out var resolutionError))
        {
            return ToolError(resolutionError);
        }

        var label = NormalizeOptionalString(arguments?["label"]?.GetValue<string>());
        if (label is null)
        {
            return ToolError("label is required.");
        }

        if (!ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "startUtc", out var startUtc, out var errorMessage)
            || !ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "endUtc", out var endUtc, out errorMessage))
        {
            return ToolError(errorMessage ?? "Invalid annotation timestamp.");
        }

        var source = NormalizeOptionalString(arguments?["source"]?.GetValue<string>()) ?? OperationDefaults.DefaultInjectedAnnotationSource;
        var annotationStartUtc = (startUtc ?? ResolveDefaultAnnotationStartUtc(snapshot!)).ToUniversalTime();
        if (!SessionAnnotationArgumentReader.TryReadGeometries(arguments, annotationStartUtc, out var geometries, out errorMessage)
            || !SessionAnnotationArgumentReader.TryReadTarget(arguments, source, out var target, out errorMessage))
        {
            return ToolError(errorMessage ?? "Invalid annotation payload.");
        }

        var annotation = new SessionAnnotation
        {
            AnnotationId = NormalizeOptionalString(arguments?["annotationId"]?.GetValue<string>()) ?? Guid.NewGuid().ToString("N"),
            StartUtc = annotationStartUtc,
            EndUtc = endUtc?.ToUniversalTime(),
            Label = label,
            Source = source,
            Notes = NormalizeOptionalString(arguments?["notes"]?.GetValue<string>()),
            Status = NormalizeOptionalString(arguments?["status"]?.GetValue<string>()),
            Geometry = geometries,
            Target = target
        };
        var result = runtimeState.UpsertSessionAnnotation(snapshot!.SessionId, annotation);
        if (!result.IsSuccess)
        {
            return ToolError(result.Message);
        }

        if (!runtimeState.TryGetSessionSnapshot(snapshot.SessionId, out var updatedSnapshot) || updatedSnapshot is null)
        {
            updatedSnapshot = snapshot;
        }

        var savedAnnotation = updatedSnapshot.Annotations.FirstOrDefault(candidate =>
            string.Equals(candidate.AnnotationId, annotation.AnnotationId, StringComparison.Ordinal)) ?? annotation;

        return RequestResult.ToolResult(
            new JsonObject
            {
                ["message"] = result.Message,
                ["sessionId"] = updatedSnapshot.SessionId,
                ["appId"] = updatedSnapshot.AppId,
                ["clientName"] = updatedSnapshot.ClientName,
                ["status"] = updatedSnapshot.Status,
                ["annotationId"] = savedAnnotation.AnnotationId,
                ["source"] = savedAnnotation.Source,
                ["annotation"] = PayloadJson.BuildSessionAnnotationPayload(savedAnnotation)
            },
            isError: false);
    }

    public static bool MatchesAnnotationTimeRange(SessionAnnotation annotation, DateTimeOffset? startUtc, DateTimeOffset? endUtc)
    {
        var effectiveEndUtc = annotation.EndUtc ?? annotation.StartUtc;
        return (!startUtc.HasValue || effectiveEndUtc >= startUtc.Value)
               && (!endUtc.HasValue || annotation.StartUtc <= endUtc.Value);
    }

    private static bool MatchesAnnotationPoint(SessionAnnotation annotation, DateTimeOffset targetUtc)
    {
        var effectiveEndUtc = annotation.EndUtc ?? annotation.StartUtc;
        return annotation.StartUtc <= targetUtc && effectiveEndUtc >= targetUtc;
    }

    private static bool MatchesAnnotationLabel(SessionAnnotation annotation, string labelQuery)
    {
        return annotation.Label.Contains(labelQuery, StringComparison.OrdinalIgnoreCase)
               || (!string.IsNullOrWhiteSpace(annotation.Notes)
                   && annotation.Notes.Contains(labelQuery, StringComparison.OrdinalIgnoreCase));
    }

    private static DateTimeOffset ResolveDefaultAnnotationStartUtc(AppSessionSnapshot snapshot)
    {
        var lastUpdatedUtc = snapshot.LastUpdatedUtc.ToUniversalTime();
        return lastUpdatedUtc > DateTimeOffset.MinValue
            ? lastUpdatedUtc
            : DateTimeOffset.UtcNow;
    }

    private static RequestResult ToolError(string message)
    {
        return RequestResult.ToolResult(
            new JsonObject
            {
                ["message"] = message
            },
            isError: true);
    }

    private static string? NormalizeOptionalString(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
