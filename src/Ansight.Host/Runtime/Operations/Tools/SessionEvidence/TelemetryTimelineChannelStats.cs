using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal sealed class TelemetryTimelineChannelStats
{
    private long sum;

    public TelemetryTimelineChannelStats(byte channelId)
    {
        ChannelId = channelId;
    }

    public byte ChannelId { get; }

    public int Count { get; private set; }

    public long Min { get; private set; }

    public long Max { get; private set; }

    public void Add(long value)
    {
        if (Count == 0)
        {
            Min = value;
            Max = value;
        }
        else
        {
            Min = Math.Min(Min, value);
            Max = Math.Max(Max, value);
        }

        Count++;
        sum += value;
    }

    public JsonObject ToPayload(IReadOnlyDictionary<byte, SessionMetricChannel> channelMap)
    {
        channelMap.TryGetValue(ChannelId, out var channel);
        return new JsonObject
        {
            ["channelId"] = ChannelId,
            ["channelName"] = channel?.Name,
            ["type"] = PayloadJson.ResolveTelemetryType(ChannelId, channelMap),
            ["source"] = channel?.Source,
            ["group"] = channel?.Group,
            ["kind"] = channel?.Kind,
            ["count"] = Count,
            ["min"] = Min,
            ["max"] = Max,
            ["average"] = Count == 0 ? 0d : sum / (double)Count
        };
    }
}
