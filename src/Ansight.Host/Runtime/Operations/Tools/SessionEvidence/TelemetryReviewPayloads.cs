using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal static class TelemetryReviewPayloads
{
    public static IReadOnlyList<SessionMetricSample> FilterSamples(
        AppSessionSnapshot snapshot,
        DateTimeOffset? startUtc,
        DateTimeOffset? endUtc,
        HashSet<string> types,
        HashSet<byte> channelIds,
        HashSet<string> channelNames)
    {
        var channelMap = snapshot.MetricChannels.ToDictionary(channel => channel.ChannelId);
        return snapshot.Metrics
            .Where(sample => PayloadJson.MatchesTimestamp(sample.CapturedAtUtc, startUtc, endUtc))
            .Where(sample => channelIds.Count == 0 || channelIds.Contains(sample.ChannelId))
            .Where(sample => types.Count == 0 || types.Contains(PayloadJson.ResolveTelemetryType(sample.ChannelId, channelMap)))
            .Where(sample =>
            {
                if (channelNames.Count == 0)
                {
                    return true;
                }

                return channelMap.TryGetValue(sample.ChannelId, out var channel)
                       && channelNames.Contains(channel.Name);
            })
            .OrderBy(sample => sample.CapturedAtUtc)
            .ThenBy(sample => sample.ChannelId)
            .ToArray();
    }

    public static JsonArray BuildChannelSummaries(
        IReadOnlyList<SessionMetricSample> samples,
        IReadOnlyDictionary<byte, SessionMetricChannel> channelMap)
    {
        return PayloadJson.CreateJsonArray(samples
            .GroupBy(sample => sample.ChannelId)
            .OrderBy(group => group.Key)
            .Select(group => (JsonNode?)BuildChannelSummary(group.Key, group.ToArray(), channelMap)));
    }

    public static JsonArray BuildTimelineBuckets(
        IReadOnlyList<SessionMetricSample> samples,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int bucketCount,
        IReadOnlyDictionary<byte, SessionMetricChannel> channelMap)
    {
        var buckets = Enumerable.Range(0, bucketCount)
            .Select(index =>
            {
                var bucketStartUtc = AddRangeFraction(startUtc, endUtc, index / (double)bucketCount);
                var bucketEndUtc = AddRangeFraction(startUtc, endUtc, (index + 1) / (double)bucketCount);
                return new TelemetryTimelineBucket(index, bucketStartUtc, bucketEndUtc);
            })
            .ToArray();

        foreach (var sample in samples)
        {
            var bucketIndex = ResolveBucketIndex(sample.CapturedAtUtc, startUtc, endUtc, bucketCount);
            buckets[bucketIndex].Add(sample);
        }

        return PayloadJson.CreateJsonArray(buckets.Select(bucket => (JsonNode?)bucket.ToPayload(channelMap)));
    }

    public static JsonObject BuildChannelSummary(
        byte channelId,
        IReadOnlyList<SessionMetricSample> samples,
        IReadOnlyDictionary<byte, SessionMetricChannel> channelMap)
    {
        channelMap.TryGetValue(channelId, out var channel);
        var orderedSamples = samples
            .OrderBy(sample => sample.CapturedAtUtc)
            .ToArray();
        var values = orderedSamples.Select(sample => sample.Value).ToArray();
        return new JsonObject
        {
            ["channelId"] = channelId,
            ["channelName"] = channel?.Name,
            ["type"] = PayloadJson.ResolveTelemetryType(channelId, channelMap),
            ["colorHex"] = channel?.ColorHex,
            ["source"] = channel?.Source,
            ["group"] = channel?.Group,
            ["kind"] = channel?.Kind,
            ["sampleCount"] = orderedSamples.Length,
            ["firstSampleUtc"] = orderedSamples.Length == 0 ? null : orderedSamples[0].CapturedAtUtc,
            ["lastSampleUtc"] = orderedSamples.Length == 0 ? null : orderedSamples[^1].CapturedAtUtc,
            ["min"] = values.Length == 0 ? null : values.Min(),
            ["max"] = values.Length == 0 ? null : values.Max(),
            ["average"] = values.Length == 0 ? null : values.Average()
        };
    }

    public static DateTimeOffset ResolveStartUtc(AppSessionSnapshot snapshot, DateTimeOffset? startUtc, IReadOnlyList<SessionMetricSample> samples)
        => startUtc ?? (samples.Count > 0 ? samples[0].CapturedAtUtc : snapshot.CreatedUtc).ToUniversalTime();

    public static DateTimeOffset ResolveEndUtc(AppSessionSnapshot snapshot, DateTimeOffset? endUtc, IReadOnlyList<SessionMetricSample> samples)
    {
        var resolved = endUtc ?? (samples.Count > 0 ? samples[^1].CapturedAtUtc : snapshot.LastUpdatedUtc).ToUniversalTime();
        return resolved;
    }

    private static int ResolveBucketIndex(DateTimeOffset timestampUtc, DateTimeOffset startUtc, DateTimeOffset endUtc, int bucketCount)
    {
        if (endUtc <= startUtc)
        {
            return 0;
        }

        var ratio = (timestampUtc - startUtc).TotalMilliseconds / (endUtc - startUtc).TotalMilliseconds;
        return Math.Clamp((int)Math.Floor(ratio * bucketCount), 0, bucketCount - 1);
    }

    private static DateTimeOffset AddRangeFraction(DateTimeOffset startUtc, DateTimeOffset endUtc, double fraction)
        => startUtc.AddMilliseconds((endUtc - startUtc).TotalMilliseconds * fraction);
}
