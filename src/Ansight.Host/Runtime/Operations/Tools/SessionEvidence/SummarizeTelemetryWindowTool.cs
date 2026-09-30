using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal sealed class SummarizeTelemetryWindowTool : Operation
{
    public SummarizeTelemetryWindowTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_summarize_telemetry_window";

    protected override string Title => "Summarize Telemetry Window";

    protected override string Description => "Summarize captured telemetry in a time window with per-channel min, max, average, and sample counts.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: TelemetryProperties(),
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!SessionReviewContext.TryResolveReviewSession(sessionResolver, arguments, out var snapshot, out var errorMessage)
            || !SessionReviewContext.TryReadTimeRange(arguments, out var startUtc, out var endUtc, out errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Invalid telemetry summary arguments."));
        }

        var types = ArgumentReader.ReadStringSet(arguments, "types");
        var channelIds = ArgumentReader.ReadByteSet(arguments, "channelIds");
        var channelNames = ArgumentReader.ReadStringSet(arguments, "channelNames");
        var channelMap = snapshot!.MetricChannels.ToDictionary(channel => channel.ChannelId);
        var samples = TelemetryReviewPayloads.FilterSamples(snapshot, startUtc, endUtc, types, channelIds, channelNames);

        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["session"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot, SessionReviewContext.IsLiveSession(sessionResolver, snapshot)),
                ["filters"] = BuildTelemetryFiltersPayload(startUtc, endUtc, types, channelIds, channelNames),
                ["matchedSampleCount"] = samples.Count,
                ["matchedChannelCount"] = samples.Select(sample => sample.ChannelId).Distinct().Count(),
                ["channels"] = PayloadJson.CreateJsonArray(snapshot.MetricChannels.Select(channel => (JsonNode?)SessionEvidencePayloads.BuildMetricChannelPayload(channel))),
                ["summaries"] = TelemetryReviewPayloads.BuildChannelSummaries(samples, channelMap)
            },
            isError: false));
    }

    private static Dictionary<string, ToolSchema> TelemetryProperties()
    {
        var properties = SessionReviewToolSchemas.ReviewTimeRangeProperties();
        properties["types"] = ToolSchema.Array(ToolSchema.String("Telemetry type, for example fps or memory."), nullable: true);
        properties["channelIds"] = ToolSchema.Array(ToolSchema.Integer("Channel id."), nullable: true);
        properties["channelNames"] = ToolSchema.Array(ToolSchema.String("Exact channel name, case-insensitive."), nullable: true);
        return properties;
    }

    private static JsonObject BuildTelemetryFiltersPayload(
        DateTimeOffset? startUtc,
        DateTimeOffset? endUtc,
        HashSet<string> types,
        HashSet<byte> channelIds,
        HashSet<string> channelNames)
    {
        return new JsonObject
        {
            ["startUtc"] = startUtc,
            ["endUtc"] = endUtc,
            ["types"] = PayloadJson.CreateJsonArray(types.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).Select(value => JsonValue.Create(value))),
            ["channelIds"] = PayloadJson.CreateJsonArray(channelIds.OrderBy(value => value).Select(value => JsonValue.Create(value))),
            ["channelNames"] = PayloadJson.CreateJsonArray(channelNames.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).Select(value => JsonValue.Create(value)))
        };
    }
}
