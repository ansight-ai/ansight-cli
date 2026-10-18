using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed class ScreenshotInspectionTool : Operation
{
    private readonly OperationServices services;
    private readonly bool evaluate;
    public ScreenshotInspectionTool(OperationServices services, bool evaluate) : base(services)
    {
        this.services = services;
        this.evaluate = evaluate;
    }
    public override string Name => evaluate ? "ansight_evaluate_screenshot" : "ansight_compare_screenshot";
    protected override string Title => evaluate ? "Evaluate Screenshot" : "Compare Screenshots";
    protected override string Description => evaluate
        ? "Evaluate a retained ACTUAL screenshot against criteria and an optional REFERENCE image using the configured AI provider. Sends the selected images to that provider and retains findings and annotation-ready regions."
        : "Compare a retained screenshot against a workspace reference image or another retained frame. Retain a difference overlay, crops and annotation-ready regions.";
    protected override JsonObject InputSchema
    {
        get
        {
            var properties = new Dictionary<string, ToolSchema>
            {
                ["sessionId"] = ToolSchema.String("Session containing the actual screenshot.", nullable: true),
                ["appId"] = ToolSchema.String("App owning the session.", nullable: true),
                ["image"] = ToolSchema.Object(properties: new Dictionary<string, ToolSchema>
                { ["frameId"] = ToolSchema.String("Exact retained screenshot frame id.") }, required: ["frameId"], additionalProperties: false),
                ["reference"] = ToolSchema.Object(description: "Exactly one frameId from this session or workspacePath relative to its ansight directory.",
                    properties: new Dictionary<string, ToolSchema>
                    {
                        ["frameId"] = ToolSchema.String("Reference frame in this session.", nullable: true),
                        ["workspacePath"] = ToolSchema.String("PNG/JPEG path relative to the ansight workspace, for example references/cubes.png.", nullable: true)
                    }, additionalProperties: false, nullable: evaluate)
            };
            if (evaluate)
            {
                properties["criteria"] = ToolSchema.Array(ToolSchema.Object(properties: new Dictionary<string, ToolSchema>
                {
                    ["id"] = ToolSchema.String("Unique criterion id."),
                    ["description"] = ToolSchema.String("What must be visible in ACTUAL, optionally relative to REFERENCE."),
                    ["count"] = ToolSchema.Object(properties: new Dictionary<string, ToolSchema>
                    {
                        ["label"] = ToolSchema.String("Object label to count, for example cube."),
                        ["attributes"] = ToolSchema.Object(description: "String attributes to match, for example color: purple.",
                            properties: new Dictionary<string, ToolSchema>(), additionalProperties: true, nullable: true),
                        ["equals"] = ToolSchema.Integer("Required count from 0 to 64.")
                    }, required: ["label", "equals"], additionalProperties: false, nullable: true)
                }, required: ["id", "description"], additionalProperties: false), description: "One to sixteen image criteria.");
                properties["reasoning"] = ToolSchema.String("Configured reasoning mode. Defaults to fast.", nullable: true, enumValues: ["fast", "balanced", "deep"]);
                properties["teamId"] = ToolSchema.String("Optional team used to authorize and meter the model call.", nullable: true);
            }
            else
            {
                properties["maxWidth"] = ToolSchema.Integer("Comparison width limit, 64–4096. Defaults to 1024.", nullable: true);
                properties["pixelThreshold"] = ToolSchema.Integer("Per-channel difference threshold, 0–255. Defaults to 16.", nullable: true);
                properties["maxDifferenceRatio"] = ToolSchema.Number("Allowed changed-pixel ratio, 0–1. Defaults to 0.01.", nullable: true);
                properties["resizeToReference"] = ToolSchema.Boolean("Explicitly resize ACTUAL to REFERENCE proportions when dimensions differ. Defaults to false.", nullable: true);
                properties["ignoreRegions"] = ScreenshotIgnoreRegions.Schema();
                properties["ignoreSystemUi"] = ScreenshotSystemUiMasks.Schema();
            }
            return ToolSchema.Object(properties: properties, required: evaluate ? ["image", "criteria"] : ["image", "reference"], additionalProperties: false).ToJson();
        }
    }

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!SessionReviewContext.TryResolveReviewSession(sessionResolver, arguments, out var session, out var error))
            return ToolError(error ?? "Select a session.");
        var token = ToolExecutionCancellation.Current;
        try
        {
            using var evidence = new ScreenshotInspectionEvidence(services, session!);
            var image = await evidence.ReadAsync(arguments?["image"] as JsonObject
                ?? throw new ArgumentException("image.frameId is required."), false, token).ConfigureAwait(false);
            var reference = arguments?["reference"] is JsonObject referenceSource
                ? await evidence.ReadAsync(referenceSource, true, token).ConfigureAwait(false) : null;
            if (arguments?["reference"] is not null && reference is null) throw new ArgumentException("reference must be an image source.");
            JsonObject result;
            if (evaluate)
            {
                var criteria = ScreenshotAssessment.ValidateCriteria(arguments?["criteria"]);
                var evaluator = services.ScreenshotEvaluator
                    ?? throw new InvalidOperationException("Screenshot evaluation requires a configured model provider on this host.");
                var reasoning = AgentReasoningModes.Normalize(arguments?["reasoning"]?.GetValue<string>() ?? "fast");
                Guid? teamId = arguments?["teamId"] is JsonNode team ? Guid.Parse(team.GetValue<string>()) : null;
                var root = ScreenshotWorkspaceScope.RepositoryRoot ?? hostAppService.Get(session!.AppId)?.CodebasePath ?? AppContext.BaseDirectory;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                var response = await evaluator.EvaluateAsync(new ScreenshotEvaluationRequest(session!.AppId, root,
                    image, reference, criteria, reasoning, teamId), timeout.Token).ConfigureAwait(false);
                result = ScreenshotAssessment.Parse(response, image, criteria);
                result["capability"] = "screenshot.evaluate";
                result["image"] = image.Describe();
                result["image"]!["artifact"] = evidence.WriteImage("actual", image);
                if (reference is not null)
                {
                    result["reference"] = reference.Describe();
                    result["reference"]!["artifact"] = evidence.WriteImage("reference", reference);
                }
                result["requestedCriteria"] = criteria;
                result["model"] = response.Model;
                result["reasoning"] = response.Reasoning;
                result["configurationRevision"] = response.ConfigurationRevision;
                result["responseId"] = response.ResponseId;
                result["usage"] = JsonSerializer.SerializeToNode(response.Tokens, JsonUtil.Compact);
                result["modelResponse"] = evidence.Write("model-response.txt", Encoding.UTF8.GetBytes(response.Text));
            }
            else
            {
                if (reference is null) throw new ArgumentException("reference is required for comparison.");
                var maxWidth = Integer(arguments, "maxWidth", 1024, 64, 4096);
                var threshold = Integer(arguments, "pixelThreshold", 16, 0, 255);
                var ratio = arguments?["maxDifferenceRatio"]?.GetValue<double>() ?? 0.01;
                if (!double.IsFinite(ratio) || ratio < 0 || ratio > 1) throw new ArgumentException("maxDifferenceRatio must be between 0 and 1.");
                var kinds = ScreenshotSystemUiMasks.Read(arguments?["ignoreSystemUi"]);
                var masks = ScreenshotSystemUiMasks.Resolve(image.Geometry, kinds,
                    ScreenshotIgnoreRegions.Read(arguments?["ignoreRegions"]), image.FrameId);
                result = ScreenshotComparison.Compare(image, reference, maxWidth, threshold, ratio,
                    arguments?["resizeToReference"]?.GetValue<bool>() ?? false,
                    masks.Regions, evidence, token);
                result["ignoreSystemUi"] = new JsonArray(kinds.Select(kind => (JsonNode?)JsonValue.Create(kind)).ToArray());
                result["systemUiMaskResolutions"] = masks.Resolutions;
            }
            token.ThrowIfCancellationRequested();
            result["sessionId"] = session!.SessionId;
            result["appId"] = session.AppId;
            result["completedAtUtc"] = DateTimeOffset.UtcNow;
            evidence.AddCrops(result["regions"]!.AsArray(), image, token);
            evidence.Persist(result, image.CapturedAtUtc ?? DateTimeOffset.UtcNow);
            // A completed comparison/assessment is data; a named expect decides task failure.
            return RequestResult.ToolResult(result, isError: false);
        }
        catch (ScreenshotGeometryUnavailableException exception)
        {
            return RequestResult.ToolResult(new JsonObject { ["errorCode"] = "screenshot_geometry_unavailable", ["message"] = exception.Message }, isError: true);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException
            or InvalidOperationException or FormatException or JsonException)
        {
            return ToolError(exception.Message);
        }
    }

    private static int Integer(JsonObject? arguments, string key, int fallback, int minimum, int maximum)
    {
        if (arguments?[key] is null) return fallback;
        if (arguments[key] is JsonValue value && value.TryGetValue<int>(out var number) && number >= minimum && number <= maximum) return number;
        throw new ArgumentException($"{key} must be an integer between {minimum} and {maximum}.");
    }
}
