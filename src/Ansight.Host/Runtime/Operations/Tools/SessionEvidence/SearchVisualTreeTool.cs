using System.Globalization;
using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal sealed class SearchVisualTreeTool : Operation
{
    public SearchVisualTreeTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_search_visual_tree";

    protected override string Title => "Search Visual Tree";

    protected override string Description => "Search captured visual tree snapshots by query, node metadata, or normalized point.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: SearchProperties(),
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!SessionReviewContext.TryResolveReviewSession(sessionResolver, arguments, out var snapshot, out var errorMessage)
            || !ArgumentReader.TryReadPositiveLimit(arguments, "limit", SessionEvidenceDefaults.DefaultVisualTreeSearchLimit, SessionEvidenceDefaults.MaxVisualTreeSearchLimit, out var limit, out errorMessage)
            || !TryReadOptionalDouble(arguments, "normalizedX", out var normalizedX, out errorMessage)
            || !TryReadOptionalDouble(arguments, "normalizedY", out var normalizedY, out errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Invalid visual tree search arguments."));
        }

        var criteria = new VisualTreeSearchCriteria
        {
            Query = NormalizeOptionalString(arguments?["query"]?.GetValue<string>()),
            SnapshotId = NormalizeOptionalString(arguments?["snapshotId"]?.GetValue<string>()),
            NodeId = NormalizeOptionalString(arguments?["nodeId"]?.GetValue<string>()),
            Label = NormalizeOptionalString(arguments?["label"]?.GetValue<string>()),
            AutomationId = NormalizeOptionalString(arguments?["automationId"]?.GetValue<string>()),
            Type = NormalizeOptionalString(arguments?["type"]?.GetValue<string>()),
            NormalizedX = normalizedX,
            NormalizedY = normalizedY,
            Limit = limit
        };

        if (normalizedX.HasValue != normalizedY.HasValue)
        {
            return Task.FromResult(ToolError("normalizedX and normalizedY must be provided together."));
        }

        var matches = VisualTreeSearch.Search(snapshot!, criteria);
        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["session"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot!, SessionReviewContext.IsLiveSession(sessionResolver, snapshot!)),
                ["visualTreeSnapshotCount"] = snapshot!.VisualTreeSnapshots.Count,
                ["returnedMatchCount"] = matches.Count,
                ["isTruncated"] = matches.Count >= limit,
                ["matches"] = matches
            },
            isError: false));
    }

    private static Dictionary<string, ToolSchema> SearchProperties()
    {
        var properties = SessionReviewToolSchemas.ReviewSessionProperties();
        properties["query"] = ToolSchema.String("Optional case-insensitive text query over id, type, label, and automationId.", nullable: true);
        properties["snapshotId"] = ToolSchema.String("Optional captured visual tree snapshot id to search.", nullable: true);
        properties["nodeId"] = ToolSchema.String("Optional exact node id filter.", nullable: true);
        properties["label"] = ToolSchema.String("Optional exact label filter.", nullable: true);
        properties["automationId"] = ToolSchema.String("Optional exact automation id filter.", nullable: true);
        properties["type"] = ToolSchema.String("Optional exact node type filter.", nullable: true);
        properties["normalizedX"] = ToolSchema.Number("Optional normalized x coordinate for point-in-bounds matching.", nullable: true);
        properties["normalizedY"] = ToolSchema.Number("Optional normalized y coordinate for point-in-bounds matching.", nullable: true);
        properties["limit"] = ToolSchema.Integer("Maximum number of matches to return. Defaults to 100, max 1000.", nullable: true);
        return properties;
    }

    private static bool TryReadOptionalDouble(JsonObject? arguments, string propertyName, out double? value, out string? errorMessage)
    {
        value = null;
        errorMessage = null;
        if (arguments?[propertyName] is null)
        {
            return true;
        }

        if (arguments[propertyName] is JsonValue jsonValue
            && (jsonValue.TryGetValue<double>(out var parsed)
                || double.TryParse(jsonValue.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)))
        {
            if (double.IsNaN(parsed) || double.IsInfinity(parsed) || parsed < 0d || parsed > 1d)
            {
                errorMessage = $"{propertyName} must be between 0.0 and 1.0.";
                return false;
            }

            value = parsed;
            return true;
        }

        errorMessage = $"{propertyName} must be a number.";
        return false;
    }
}
