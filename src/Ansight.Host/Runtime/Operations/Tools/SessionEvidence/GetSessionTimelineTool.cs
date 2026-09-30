using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal sealed class GetSessionTimelineTool : Operation
{
    public GetSessionTimelineTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_get_session_timeline";

    protected override string Title => "Get Session Timeline";

    protected override string Description => "Return a unified chronological stream of logs, network requests, screenshots, touches, telemetry, annotations, visual trees, artifact snapshots, and app state.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: TimelineProperties(),
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!SessionReviewContext.TryResolveReviewSession(sessionResolver, arguments, out var snapshot, out var errorMessage)
            || !SessionReviewContext.TryReadTimeRange(arguments, out var startUtc, out var endUtc, out errorMessage)
            || !ArgumentReader.TryReadPositiveLimit(arguments, "limit", SessionEvidenceDefaults.DefaultTimelineLimit, SessionEvidenceDefaults.MaxTimelineLimit, out var limit, out errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Invalid session timeline arguments."));
        }

        var categoryFilters = ArgumentReader.ReadStringSet(arguments, "categories");
        if (!TryReadPageOffset(arguments, out var offset, out errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Invalid timeline cursor."));
        }

        var actionId = NormalizeOptionalString(arguments?["actionId"]?.GetValue<string>());
        var events = SessionTimelineBuilder.BuildEvents(snapshot!, startUtc, endUtc)
            .Where(timelineEvent => categoryFilters.Count == 0
                                    || categoryFilters.Contains(timelineEvent.Payload["category"]?.GetValue<string>() ?? string.Empty))
            .Where(timelineEvent => actionId is null || IsCorrelatedWithAction(snapshot!, timelineEvent, actionId))
            .ToArray();
        var returnedEvents = events.Skip(offset).Take(limit).ToArray();
        var nextOffset = offset + returnedEvents.Length;
        var hasMore = nextOffset < events.Length;
        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["session"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot!, SessionReviewContext.IsLiveSession(sessionResolver, snapshot!)),
                ["actionId"] = actionId,
                ["matchedEventCount"] = events.Length,
                ["returnedEventCount"] = returnedEvents.Length,
                ["pageOffset"] = offset,
                ["hasMore"] = hasMore,
                ["nextCursor"] = hasMore ? EncodeCursor(nextOffset) : null,
                ["isTruncated"] = offset > 0 || hasMore,
                ["events"] = PayloadJson.CreateJsonArray(returnedEvents.Select(timelineEvent => (JsonNode?)timelineEvent.Payload))
            },
            isError: false));
    }

    private static Dictionary<string, ToolSchema> TimelineProperties()
    {
        var properties = SessionReviewToolSchemas.ReviewTimeRangeProperties();
        properties["categories"] = ToolSchema.Array(
            ToolSchema.String(
                "Timeline category to include.",
                enumValues:
                [
                    "session",
                    "appState",
                    "applicationEvent",
                    "log",
                    "networkRequest",
                    "screenshot",
                    "touch",
                    "annotation",
                    "visualTree",
                    "uiAction",
                    "telemetry",
                    "artifactSnapshot"
                ]),
            description: "Optional normalized timeline category filters.",
            nullable: true);
        properties["limit"] = ToolSchema.Integer("Maximum number of timeline events to return. Defaults to 1000, max 10000.", nullable: true);
        properties["cursor"] = ToolSchema.String("Opaque cursor returned by a previous page.", nullable: true);
        properties["offset"] = ToolSchema.Integer("Zero-based page offset when no cursor is supplied.", nullable: true);
        properties["actionId"] = ToolSchema.String("Return the correlated action, before/after trees, screenshots, and action-tagged logs.", nullable: true);
        return properties;
    }

    internal static bool TryReadPageOffset(JsonObject? arguments, out int offset, out string? errorMessage)
    {
        offset = 0;
        errorMessage = null;
        var cursor = NormalizeOptionalString(arguments?["cursor"]?.GetValue<string>());
        if (cursor is not null)
        {
            try
            {
                var encoded = cursor.Replace('-', '+').Replace('_', '/');
                encoded = encoded.PadRight(encoded.Length + ((4 - (encoded.Length % 4)) % 4), '=');
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                if (!decoded.StartsWith("timeline:v1:", StringComparison.Ordinal)
                    || !int.TryParse(decoded["timeline:v1:".Length..], out offset)
                    || offset < 0)
                {
                    throw new FormatException();
                }

                return true;
            }
            catch (FormatException)
            {
                errorMessage = "cursor is not a valid Ansight timeline cursor.";
                return false;
            }
        }

        if (arguments?["offset"] is JsonValue offsetValue)
        {
            if (!offsetValue.TryGetValue<int>(out offset) || offset < 0)
            {
                errorMessage = "offset must be a non-negative integer.";
                return false;
            }
        }

        return true;
    }

    internal static string EncodeCursor(int offset)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes($"timeline:v1:{offset}");
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool IsCorrelatedWithAction(
        AppSessionSnapshot snapshot,
        SessionTimelineEvent timelineEvent,
        string actionId)
    {
        var category = timelineEvent.Payload["category"]?.GetValue<string>();
        var details = timelineEvent.Payload["details"] as JsonObject;
        if (string.Equals(details?["actionId"]?.GetValue<string>(), actionId, StringComparison.Ordinal)
            || string.Equals(details?["eventId"]?.GetValue<string>(), actionId, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(category, "screenshot", StringComparison.Ordinal))
        {
            return false;
        }

        var frameId = details?["frameId"]?.GetValue<string>();
        return snapshot.VisualTreeSnapshots.Any(visualTree =>
            string.Equals(visualTree.ActionId, actionId, StringComparison.Ordinal)
            && string.Equals(visualTree.ScreenshotFrameId, frameId, StringComparison.Ordinal));
    }
}
