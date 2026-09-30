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

    protected override string Description => "Bucket captured telemetry over time with per-channel min, max, average, and sample counts.";

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

        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["session"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot, SessionReviewContext.IsLiveSession(sessionResolver, snapshot)),
                ["matchedSampleCount"] = samples.Count,
                ["startUtc"] = resolvedStartUtc,
                ["endUtc"] = resolvedEndUtc,
                ["bucketCount"] = bucketCount,
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
