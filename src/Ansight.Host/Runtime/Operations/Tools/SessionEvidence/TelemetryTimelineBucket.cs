using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal sealed class TelemetryTimelineBucket
{
    private readonly Dictionary<byte, TelemetryTimelineChannelStats> statsByChannelId = new();

    public TelemetryTimelineBucket(int index, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        Index = index;
        StartUtc = startUtc;
        EndUtc = endUtc;
    }

    public int Index { get; }

    public DateTimeOffset StartUtc { get; }

    public DateTimeOffset EndUtc { get; }

    public int SampleCount { get; private set; }

    public void Add(SessionMetricSample sample)
    {
        SampleCount++;
        if (!statsByChannelId.TryGetValue(sample.ChannelId, out var stats))
        {
            stats = new TelemetryTimelineChannelStats(sample.ChannelId);
            statsByChannelId[sample.ChannelId] = stats;
        }

        stats.Add(sample.Value);
    }

    public JsonObject ToPayload(IReadOnlyDictionary<byte, SessionMetricChannel> channelMap)
    {
        return new JsonObject
        {
            ["index"] = Index,
            ["startUtc"] = StartUtc,
            ["endUtc"] = EndUtc,
            ["sampleCount"] = SampleCount,
            ["channels"] = PayloadJson.CreateJsonArray(statsByChannelId
                .OrderBy(entry => entry.Key)
                .Select(entry => (JsonNode?)entry.Value.ToPayload(channelMap)))
        };
    }
}
