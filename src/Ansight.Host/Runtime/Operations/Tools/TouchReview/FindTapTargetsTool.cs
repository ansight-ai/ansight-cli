using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal sealed class FindTapTargetsTool : Operation
{
    public FindTapTargetsTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_find_tap_targets";

    protected override string Title => "Find Tap Targets";

    protected override string Description => "Match tap and long-press gestures to annotation geometry and visual tree elements near the touch time.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: TapTargetProperties(),
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!SessionReviewContext.TryResolveReviewSession(sessionResolver, arguments, out var snapshot, out var errorMessage)
            || !TouchReviewArgumentReader.TryReadTouchFilters(arguments, out var filters, out errorMessage)
            || !TouchReviewArgumentReader.TryReadBoundedPositiveInteger(
                arguments,
                "windowMs",
                TouchReviewDefaults.DefaultTapTargetWindowMilliseconds,
                TouchReviewDefaults.MaxGestureGapMilliseconds,
                out var windowMs,
                out errorMessage)
            || !TouchReviewArgumentReader.TryReadBooleanArgument(arguments, "includeUntargeted", defaultValue: true, out var includeUntargeted, out errorMessage)
            || !ArgumentReader.TryReadPositiveLimit(
                arguments,
                "limit",
                TouchReviewDefaults.DefaultTouchResultLimit,
                TouchReviewDefaults.MaxTouchResultLimit,
                out var limit,
                out errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Invalid tap target arguments."));
        }

        var touches = TouchReviewFiltering.GetFilteredTouches(snapshot!, filters).ToArray();
        var gestures = TouchGestureSegmenter.BuildGestureSegments(touches, TimeSpan.FromMilliseconds(TouchReviewDefaults.DefaultGestureGapMilliseconds))
            .Where(gesture => gesture.Kind is "tap" or "longPress")
            .ToArray();
        var matches = new List<JsonObject>();
        foreach (var gesture in gestures)
        {
            var point = TouchReviewGeometry.ResolveGesturePoint(gesture);
            if (point is null)
            {
                if (includeUntargeted)
                {
                    matches.Add(TapTargetMatcher.BuildTapTargetMatchPayload(gesture, point, []));
                }

                continue;
            }

            var targetCandidates = TapTargetMatcher.FindTapTargetCandidates(snapshot!, gesture, point.Value, TimeSpan.FromMilliseconds(windowMs)).ToArray();
            if (targetCandidates.Length > 0 || includeUntargeted)
            {
                matches.Add(TapTargetMatcher.BuildTapTargetMatchPayload(gesture, point, targetCandidates));
            }
        }

        var returnedMatches = matches.Take(limit).ToArray();
        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["session"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot!, SessionReviewContext.IsLiveSession(sessionResolver, snapshot!)),
                ["filters"] = TouchReviewFiltering.BuildTouchFiltersPayload(filters),
                ["matchedTapCount"] = matches.Count,
                ["returnedTapCount"] = returnedMatches.Length,
                ["isTruncated"] = matches.Count > returnedMatches.Length,
                ["tapTargets"] = PayloadJson.CreateJsonArray(returnedMatches)
            },
            isError: false));
    }

    private static Dictionary<string, ToolSchema> TapTargetProperties()
    {
        var properties = SessionReviewToolSchemas.TouchFilterProperties();
        properties["windowMs"] = ToolSchema.Integer("Milliseconds around each gesture to search annotations and visual tree snapshots. Defaults to 1500, max 10000.", nullable: true);
        properties["includeUntargeted"] = ToolSchema.Boolean("Include taps with no matched target candidates. Defaults to true.", nullable: true);
        properties["limit"] = ToolSchema.Integer("Maximum number of tap matches to return. Defaults to 500, max 5000.", nullable: true);
        return properties;
    }
}
