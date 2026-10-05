using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal sealed class UpdateAnnotationTool : Operation
{
    public UpdateAnnotationTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_update_annotation";

    protected override string Title => "Update Session Annotation";

    protected override string Description => "Update an existing session annotation, optionally guarding by its current source.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific session id to annotate.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one matching session exists.", nullable: true),
            ["includeHistorical"] = ToolSchema.Boolean("When resolving by appId, include historical sessions. Defaults to true.", nullable: true),
            ["annotationId"] = ToolSchema.String("Required annotation id to update."),
            ["expectedSource"] = ToolSchema.String("Optional source guard; the update is rejected if the existing annotation source differs.", nullable: true),
            ["source"] = ToolSchema.String("Optional replacement annotation source. Existing source is preserved when omitted.", nullable: true),
            ["label"] = ToolSchema.String("Optional replacement annotation label.", nullable: true),
            ["notes"] = ToolSchema.String("Optional replacement annotation notes.", nullable: true),
            ["status"] = ToolSchema.String("Optional free-text annotation status, such as resolved.", nullable: true),
            ["clearStatus"] = ToolSchema.Boolean("Clear the annotation status.", nullable: true),
            ["clearNotes"] = ToolSchema.Boolean("Clear annotation notes.", nullable: true),
            ["startUtc"] = ToolSchema.String("Optional replacement annotation start timestamp.", nullable: true, format: "date-time"),
            ["endUtc"] = ToolSchema.String("Optional replacement annotation end timestamp.", nullable: true, format: "date-time"),
            ["clearEndUtc"] = ToolSchema.Boolean("Clear the annotation end timestamp.", nullable: true),
            ["geometries"] = ToolSchema.Array(
                SessionInspectionToolSchemas.AnnotationGeometrySchema(),
                description: "Optional replacement screenshot geometry entries.",
                nullable: true),
            ["target"] = ToolSchema.Object(
                properties: SessionInspectionToolSchemas.AnnotationTargetSchema().Properties,
                additionalProperties: false,
                nullable: true),
            ["clearTarget"] = ToolSchema.Boolean("Clear the annotation target.", nullable: true)
        },
        required: ["annotationId"],
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!sessionResolver.TryResolveSession(arguments, requireLiveSession: false, out var snapshot, out var resolutionError))
        {
            return Task.FromResult(ToolError(resolutionError));
        }

        var annotationId = NormalizeOptionalString(arguments?["annotationId"]?.GetValue<string>());
        if (annotationId is null)
        {
            return Task.FromResult(ToolError("annotationId is required."));
        }

        var existingAnnotation = snapshot!.Annotations.FirstOrDefault(annotation =>
            string.Equals(annotation.AnnotationId, annotationId, StringComparison.Ordinal));
        if (existingAnnotation is null)
        {
            return Task.FromResult(ToolError($"Annotation '{annotationId}' was not found for session '{snapshot.SessionId}'."));
        }

        var expectedSource = NormalizeOptionalString(arguments?["expectedSource"]?.GetValue<string>());
        if (expectedSource is not null && !string.Equals(existingAnnotation.Source, expectedSource, StringComparison.Ordinal))
        {
            return Task.FromResult(ToolError($"Annotation '{annotationId}' source is '{existingAnnotation.Source}', not '{expectedSource}'."));
        }

        if (!ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "startUtc", out var startUtc, out var errorMessage)
            || !ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "endUtc", out var endUtc, out errorMessage)
            || !ArgumentReader.TryReadOptionalBooleanArgument(arguments, "clearNotes", out var clearNotes, out errorMessage)
            || !ArgumentReader.TryReadOptionalBooleanArgument(arguments, "clearStatus", out var clearStatus, out errorMessage)
            || !ArgumentReader.TryReadOptionalBooleanArgument(arguments, "clearEndUtc", out var clearEndUtc, out errorMessage)
            || !ArgumentReader.TryReadOptionalBooleanArgument(arguments, "clearTarget", out var clearTarget, out errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Invalid annotation update arguments."));
        }

        var updatedStartUtc = (startUtc ?? existingAnnotation.StartUtc).ToUniversalTime();
        var updatedEndUtc = clearEndUtc == true
            ? null
            : (endUtc?.ToUniversalTime() ?? existingAnnotation.EndUtc);
        var updatedNotes = clearNotes == true
            ? null
            : arguments?["notes"] is null
                ? existingAnnotation.Notes
                : NormalizeOptionalString(arguments?["notes"]?.GetValue<string>());
        var updatedStatus = clearStatus == true
            ? null
            : arguments?.ContainsKey("status") == true
                ? NormalizeOptionalString(arguments["status"]?.GetValue<string>())
                : existingAnnotation.Status;

        var updatedGeometry = existingAnnotation.Geometry;
        if (arguments?["geometries"] is not null)
        {
            if (!SessionAnnotationArgumentReader.TryReadGeometries(arguments, updatedStartUtc, out updatedGeometry, out errorMessage))
            {
                return Task.FromResult(ToolError(errorMessage ?? "Invalid annotation geometry."));
            }
        }

        var updatedTarget = existingAnnotation.Target;
        if (clearTarget == true)
        {
            updatedTarget = null;
        }
        else if (arguments?["target"] is not null)
        {
            if (!SessionAnnotationArgumentReader.TryReadTarget(arguments, NormalizeOptionalString(arguments?["source"]?.GetValue<string>()) ?? existingAnnotation.Source, out updatedTarget, out errorMessage))
            {
                return Task.FromResult(ToolError(errorMessage ?? "Invalid annotation target."));
            }
        }

        var updatedAnnotation = new SessionAnnotation
        {
            AnnotationId = existingAnnotation.AnnotationId,
            StartUtc = updatedStartUtc,
            EndUtc = updatedEndUtc,
            Label = NormalizeOptionalString(arguments?["label"]?.GetValue<string>()) ?? existingAnnotation.Label,
            Source = NormalizeOptionalString(arguments?["source"]?.GetValue<string>()) ?? existingAnnotation.Source,
            Notes = updatedNotes,
            Status = updatedStatus,
            CaptureGroupId = existingAnnotation.CaptureGroupId,
            CustomData = existingAnnotation.CustomData?.DeepClone() as JsonObject,
            Evidence = existingAnnotation.Evidence.Select(SessionSnapshotCloner.CloneAnnotationEvidence).ToArray(),
            HookFailures = existingAnnotation.HookFailures.ToArray(),
            Geometry = updatedGeometry,
            Target = updatedTarget
        };

        var result = runtimeState.UpsertSessionAnnotation(snapshot.SessionId, updatedAnnotation);
        if (!result.IsSuccess)
        {
            return Task.FromResult(ToolError(result.Message));
        }

        if (!runtimeState.TryGetSessionSnapshot(snapshot.SessionId, out var updatedSnapshot) || updatedSnapshot is null)
        {
            updatedSnapshot = snapshot;
        }

        var savedAnnotation = updatedSnapshot.Annotations.FirstOrDefault(annotation =>
            string.Equals(annotation.AnnotationId, updatedAnnotation.AnnotationId, StringComparison.Ordinal)) ?? updatedAnnotation;
        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["message"] = result.Message,
                ["sessionId"] = updatedSnapshot.SessionId,
                ["appId"] = updatedSnapshot.AppId,
                ["annotationId"] = savedAnnotation.AnnotationId,
                ["source"] = savedAnnotation.Source,
                ["annotation"] = PayloadJson.BuildSessionAnnotationPayload(savedAnnotation)
            },
            isError: false));
    }
}
