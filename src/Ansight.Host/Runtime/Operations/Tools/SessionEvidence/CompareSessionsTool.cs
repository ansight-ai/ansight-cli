using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal sealed class CompareSessionsTool : Operation
{
    public CompareSessionsTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_compare_sessions";

    protected override string Title => "Compare Sessions";

    protected override string Description => "Compare two captured sessions for differences in evidence counts, log priorities, telemetry ranges, touch flow, screenshots, and artifacts.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["leftSessionId"] = ToolSchema.String("Baseline or earlier session id."),
            ["rightSessionId"] = ToolSchema.String("Comparison or later session id.")
        },
        required: ["leftSessionId", "rightSessionId"],
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        var leftSessionId = NormalizeOptionalString(arguments?["leftSessionId"]?.GetValue<string>());
        var rightSessionId = NormalizeOptionalString(arguments?["rightSessionId"]?.GetValue<string>());
        if (leftSessionId is null || rightSessionId is null)
        {
            return Task.FromResult(ToolError("leftSessionId and rightSessionId are required."));
        }

        if (!sessionResolver.TryLoadResolvedSnapshot(leftSessionId, out var leftSnapshot, out var leftError)
            || leftSnapshot is null)
        {
            return Task.FromResult(ToolError(leftError));
        }

        if (!sessionResolver.TryLoadResolvedSnapshot(rightSessionId, out var rightSnapshot, out var rightError)
            || rightSnapshot is null)
        {
            return Task.FromResult(ToolError(rightError));
        }

        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["leftSession"] = SessionReviewContext.BuildSessionHeaderPayload(leftSnapshot, SessionReviewContext.IsLiveSession(sessionResolver, leftSnapshot)),
                ["rightSession"] = SessionReviewContext.BuildSessionHeaderPayload(rightSnapshot, SessionReviewContext.IsLiveSession(sessionResolver, rightSnapshot)),
                ["countDeltas"] = BuildCountDeltas(leftSnapshot, rightSnapshot),
                ["logPriorityDeltas"] = BuildLogPriorityDeltas(leftSnapshot, rightSnapshot),
                ["telemetryDeltas"] = BuildTelemetryDeltas(leftSnapshot, rightSnapshot),
                ["touchDeltas"] = BuildTouchDeltas(leftSnapshot, rightSnapshot)
            },
            isError: false));
    }

    private static JsonObject BuildCountDeltas(AppSessionSnapshot left, AppSessionSnapshot right)
    {
        return new JsonObject
        {
            ["logs"] = BuildDelta(left.Logs.Count, right.Logs.Count),
            ["screenshots"] = BuildDelta(left.Images.Count, right.Images.Count),
            ["touches"] = BuildDelta(left.Touches.Count, right.Touches.Count),
            ["annotations"] = BuildDelta(left.Annotations.Count, right.Annotations.Count),
            ["visualTrees"] = BuildDelta(left.VisualTreeSnapshots.Count, right.VisualTreeSnapshots.Count),
            ["artifactSnapshots"] = BuildDelta(left.ArtifactSnapshots.Count, right.ArtifactSnapshots.Count),
            ["metricSamples"] = BuildDelta(left.Metrics.Count, right.Metrics.Count)
        };
    }

    private static JsonArray BuildLogPriorityDeltas(AppSessionSnapshot left, AppSessionSnapshot right)
    {
        var priorities = left.Logs.Select(log => log.Priority)
            .Concat(right.Logs.Select(log => log.Priority))
            .Distinct()
            .OrderBy(priority => priority.ToString())
            .ToArray();
        return PayloadJson.CreateJsonArray(priorities.Select(priority => (JsonNode?)new JsonObject
        {
            ["priority"] = priority.ToString(),
            ["counts"] = BuildDelta(
                left.Logs.Count(log => log.Priority == priority),
                right.Logs.Count(log => log.Priority == priority))
        }));
    }

    private static JsonArray BuildTelemetryDeltas(AppSessionSnapshot left, AppSessionSnapshot right)
    {
        var leftChannelMap = left.MetricChannels.ToDictionary(channel => channel.ChannelId);
        var rightChannelMap = right.MetricChannels.ToDictionary(channel => channel.ChannelId);
        var channelNames = left.MetricChannels.Select(channel => channel.Name)
            .Concat(right.MetricChannels.Select(channel => channel.Name))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return PayloadJson.CreateJsonArray(channelNames.Select(channelName => (JsonNode?)new JsonObject
        {
            ["channelName"] = channelName,
            ["left"] = BuildTelemetrySummaryForChannel(left, leftChannelMap, channelName),
            ["right"] = BuildTelemetrySummaryForChannel(right, rightChannelMap, channelName)
        }));
    }

    private static JsonObject BuildTouchDeltas(AppSessionSnapshot left, AppSessionSnapshot right)
    {
        var leftGestures = TouchGestureSegmenter.BuildGestureSegments(left.Touches, TimeSpan.FromMilliseconds(TouchReviewDefaults.DefaultGestureGapMilliseconds));
        var rightGestures = TouchGestureSegmenter.BuildGestureSegments(right.Touches, TimeSpan.FromMilliseconds(TouchReviewDefaults.DefaultGestureGapMilliseconds));
        return new JsonObject
        {
            ["gestureCount"] = BuildDelta(leftGestures.Count, rightGestures.Count),
            ["incompleteGestureCount"] = BuildDelta(leftGestures.Count(gesture => !gesture.IsComplete), rightGestures.Count(gesture => !gesture.IsComplete)),
            ["gestureKindDeltas"] = BuildStringDeltas(leftGestures.Select(gesture => gesture.Kind), rightGestures.Select(gesture => gesture.Kind))
        };
    }

    private static JsonObject BuildTelemetrySummaryForChannel(
        AppSessionSnapshot snapshot,
        IReadOnlyDictionary<byte, SessionMetricChannel> channelMap,
        string channelName)
    {
        var samples = snapshot.Metrics
            .Where(sample => channelMap.TryGetValue(sample.ChannelId, out var channel)
                             && string.Equals(channel.Name, channelName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (samples.Length == 0)
        {
            return new JsonObject
            {
                ["sampleCount"] = 0
            };
        }

        var values = samples.Select(sample => sample.Value).ToArray();
        return new JsonObject
        {
            ["sampleCount"] = samples.Length,
            ["min"] = values.Min(),
            ["max"] = values.Max(),
            ["average"] = values.Average()
        };
    }

    private static JsonArray BuildStringDeltas(IEnumerable<string> leftValues, IEnumerable<string> rightValues)
    {
        var leftCounts = leftValues.GroupBy(value => value, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var rightCounts = rightValues.GroupBy(value => value, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var keys = leftCounts.Keys.Concat(rightCounts.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(key => key, StringComparer.OrdinalIgnoreCase);
        return PayloadJson.CreateJsonArray(keys.Select(key => (JsonNode?)new JsonObject
        {
            ["value"] = key,
            ["counts"] = BuildDelta(leftCounts.GetValueOrDefault(key), rightCounts.GetValueOrDefault(key))
        }));
    }

    private static JsonObject BuildDelta(int left, int right)
    {
        return new JsonObject
        {
            ["left"] = left,
            ["right"] = right,
            ["delta"] = right - left
        };
    }
}
