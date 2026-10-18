using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal sealed class GetTelemetryTimelineTool : Operation
{
    public GetTelemetryTimelineTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_get_telemetry_timeline";

    protected override string Title => "Get Telemetry Timeline";

    protected override string Description => "Bucket captured telemetry over time with per-channel min, max, average, units, and sample counts; include timed spans and incomplete operations.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: TimelineProperties(),
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!SessionReviewContext.TryResolveReviewSession(sessionResolver, arguments, out var snapshot, out var errorMessage)
            || !SessionReviewContext.TryReadTimeRange(arguments, out var startUtc, out var endUtc, out errorMessage)
            || !ArgumentReader.TryReadPositiveLimit(arguments, "bucketCount", SessionEvidenceDefaults.DefaultBucketCount, SessionEvidenceDefaults.MaxBucketCount, out var bucketCount, out errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Invalid telemetry timeline arguments."));
        }

        var types = ArgumentReader.ReadStringSet(arguments, "types");
        types.Remove("all");
        var channelIds = ArgumentReader.ReadByteSet(arguments, "channelIds");
        var channelNames = ArgumentReader.ReadStringSet(arguments, "channelNames");
        var channelMap = snapshot!.MetricChannels.ToDictionary(channel => channel.ChannelId);
        var samples = TelemetryReviewPayloads.FilterSamples(snapshot, startUtc, endUtc, types, channelIds, channelNames);
        var resolvedStartUtc = TelemetryReviewPayloads.ResolveStartUtc(snapshot, startUtc, samples);
        var resolvedEndUtc = TelemetryReviewPayloads.ResolveEndUtc(snapshot, endUtc, samples);
        if (resolvedEndUtc <= resolvedStartUtc)
        {
            resolvedEndUtc = resolvedStartUtc.AddMilliseconds(1);
        }

        var spans = SessionSpan.FromEvents(snapshot.ApplicationEvents)
            .Where(span => types.Count == 0 || types.Contains("span") || types.Contains("spans"))
            .Where(span => channelIds.Count == 0 || channelIds.Contains(span.ChannelId))
            .Where(span => channelNames.Count == 0 || channelMap.TryGetValue(span.ChannelId, out var channel) && channelNames.Contains(channel.Name))
            .Where(span => (!endUtc.HasValue || span.StartedAtUtc <= endUtc.Value)
                && (!startUtc.HasValue || span.DurationMs is null || span.StartedAtUtc.AddMilliseconds(span.DurationMs.Value) >= startUtc.Value))
            .ToArray();

        if (spans.Length > 0)
        {
            if (!startUtc.HasValue) resolvedStartUtc = new[] { resolvedStartUtc, spans.Min(span => span.StartedAtUtc) }.Min();
            if (!endUtc.HasValue) resolvedEndUtc = new[] { resolvedEndUtc, spans.Max(span => span.DurationMs is { } duration
                ? span.StartedAtUtc.AddMilliseconds(duration) : snapshot.LastUpdatedUtc) }.Max();
        }

        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["session"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot, SessionReviewContext.IsLiveSession(sessionResolver, snapshot)),
                ["matchedSampleCount"] = samples.Count,
                ["startUtc"] = resolvedStartUtc,
                ["endUtc"] = resolvedEndUtc,
                ["bucketCount"] = bucketCount,
                ["matchedSpanCount"] = spans.Length,
                ["spansTruncated"] = spans.Length > 500,
                ["spans"] = PayloadJson.CreateJsonArray(spans.Take(500).Select(span => (JsonNode?)new JsonObject
                {
                    ["spanId"] = span.SpanId,
                    ["parentSpanId"] = span.ParentSpanId,
                    ["name"] = span.Name,
                    ["channelId"] = span.ChannelId,
                    ["channelName"] = channelMap.GetValueOrDefault(span.ChannelId)?.Name,
                    ["startedAtUtc"] = span.StartedAtUtc,
                    ["durationMs"] = span.DurationMs,
                    ["status"] = span.Status
                })),
                ["buckets"] = TelemetryReviewPayloads.BuildTimelineBuckets(samples, resolvedStartUtc, resolvedEndUtc, bucketCount, channelMap)
            },
            isError: false));
    }

    private static Dictionary<string, ToolSchema> TimelineProperties()
    {
        var properties = SessionReviewToolSchemas.ReviewTimeRangeProperties();
        properties["types"] = ToolSchema.Array(ToolSchema.String("Telemetry type, for example fps or memory."), nullable: true);
        properties["channelIds"] = ToolSchema.Array(ToolSchema.Integer("Channel id."), nullable: true);
        properties["channelNames"] = ToolSchema.Array(ToolSchema.String("Exact channel name, case-insensitive."), nullable: true);
        properties["bucketCount"] = ToolSchema.Integer("Number of timeline buckets. Defaults to 24, max 200.", nullable: true);
        return properties;
    }
}
