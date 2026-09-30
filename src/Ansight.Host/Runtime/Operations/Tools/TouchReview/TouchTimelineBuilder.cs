using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal static class TouchTimelineBuilder
{
    public static JsonArray BuildTouchTimelineBuckets(
        IReadOnlyList<SessionTouchInputRecord> touches,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int bucketCount)
    {
        var buckets = Enumerable.Range(0, bucketCount)
            .Select(index =>
            {
                var bucketStartUtc = AddRangeFraction(startUtc, endUtc, index / (double)bucketCount);
                var bucketEndUtc = AddRangeFraction(startUtc, endUtc, (index + 1) / (double)bucketCount);
                return new TouchTimelineBucket(index, bucketStartUtc, bucketEndUtc);
            })
            .ToArray();

        foreach (var touch in touches)
        {
            var bucketIndex = ResolveBucketIndex(touch.CapturedAtUtc, startUtc, endUtc, bucketCount);
            buckets[bucketIndex].Add(touch);
        }

        return PayloadJson.CreateJsonArray(buckets.Select(bucket => (JsonNode?)bucket.ToPayload()));
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
    {
        return startUtc.AddMilliseconds((endUtc - startUtc).TotalMilliseconds * fraction);
    }
}
