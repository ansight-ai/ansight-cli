namespace Ansight.Host.Models.Metrics;

public static class MetricChartPreparation
{
    private static readonly TimeSpan LiveVisibleWindow = TimeSpan.FromMinutes(2);

    public static MetricChartPreparedState Prepare(
        MetricChartState state,
        int targetRenderSamplesPerSeries,
        IReadOnlySet<byte>? visibleChannelIds = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetRenderSamplesPerSeries);

        if (state.Metrics.Count == 0 || visibleChannelIds is { Count: 0 })
        {
            return MetricChartPreparedState.Empty;
        }

        var orderedMetrics = EnsureOrdered(state.Metrics);
        var filteredMetrics = visibleChannelIds is null
            ? orderedMetrics
            : orderedMetrics.Where(sample => visibleChannelIds.Contains(sample.ChannelId)).ToArray();
        if (filteredMetrics.Length == 0)
        {
            return MetricChartPreparedState.Empty;
        }

        var latestVisibleUtc = filteredMetrics[^1].CapturedAtUtc;
        var earliestVisibleUtc = state.IsLive
            ? latestVisibleUtc - LiveVisibleWindow
            : filteredMetrics[0].CapturedAtUtc;
        var startIndex = ResolveFirstIndexAtOrAfter(filteredMetrics, earliestVisibleUtc);
        if (startIndex >= filteredMetrics.Length)
        {
            return MetricChartPreparedState.Empty with
            {
                EarliestVisibleUtc = earliestVisibleUtc,
                LatestVisibleUtc = latestVisibleUtc
            };
        }

        var groupedSamples = new Dictionary<byte, List<SessionMetricSample>>();
        long maxMemoryValue = 1;
        long maxFpsValue = 1;
        var hasMemoryMetrics = false;
        var hasFpsMetrics = false;

        for (var index = startIndex; index < filteredMetrics.Length; index++)
        {
            var sample = filteredMetrics[index];
            if (!groupedSamples.TryGetValue(sample.ChannelId, out var samples))
            {
                samples = new List<SessionMetricSample>();
                groupedSamples[sample.ChannelId] = samples;
            }

            samples.Add(sample);

            if (MetricChannelClassification.IsFpsChannel(state.Channels, sample.ChannelId))
            {
                hasFpsMetrics = true;
                maxFpsValue = Math.Max(maxFpsValue, sample.Value);
            }
            else
            {
                hasMemoryMetrics = true;
                maxMemoryValue = Math.Max(maxMemoryValue, sample.Value);
            }
        }

        var series = groupedSamples
            .Select(group =>
            {
                var isFpsChannel = MetricChannelClassification.IsFpsChannel(state.Channels, group.Key);
                var visibleSamples = group.Value.ToArray();
                var visibleSegments = CreateSegments(visibleSamples);
                var renderSegments = ReduceSegments(visibleSegments, targetRenderSamplesPerSeries);
                return new MetricChartPreparedSeries(
                    group.Key,
                    isFpsChannel,
                    visibleSamples,
                    FlattenSegments(renderSegments),
                    visibleSegments,
                    renderSegments);
            })
            .OrderBy(group => group.IsFpsChannel ? 1 : 0)
            .ThenBy(group => group.ChannelId)
            .ToArray();

        return new MetricChartPreparedState(
            earliestVisibleUtc,
            latestVisibleUtc,
            series,
            hasMemoryMetrics,
            hasFpsMetrics,
            Math.Max(1d, maxMemoryValue * 1.1d),
            Math.Max(1d, maxFpsValue * 1.1d));
    }

    private static SessionMetricSample[] EnsureOrdered(IReadOnlyList<SessionMetricSample> metrics)
    {
        if (metrics is SessionMetricSample[] array && IsOrdered(array))
        {
            return array;
        }

        var ordered = metrics.ToArray();
        Array.Sort(ordered, CompareSamples);
        return ordered;
    }

    private static bool IsOrdered(IReadOnlyList<SessionMetricSample> metrics)
    {
        for (var index = 1; index < metrics.Count; index++)
        {
            if (CompareSamples(metrics[index - 1], metrics[index]) > 0)
            {
                return false;
            }
        }

        return true;
    }

    private static int CompareSamples(SessionMetricSample? left, SessionMetricSample? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        var timestampComparison = left.CapturedAtUtc.CompareTo(right.CapturedAtUtc);
        if (timestampComparison != 0)
        {
            return timestampComparison;
        }

        return left.ChannelId.CompareTo(right.ChannelId);
    }

    private static int ResolveFirstIndexAtOrAfter(IReadOnlyList<SessionMetricSample> samples, DateTimeOffset timestampUtc)
    {
        var low = 0;
        var high = samples.Count - 1;

        while (low <= high)
        {
            var mid = low + ((high - low) >> 1);
            if (samples[mid].CapturedAtUtc < timestampUtc)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }

    private static SessionMetricSample[] ReduceSamples(IReadOnlyList<SessionMetricSample> samples, int targetRenderSamplesPerSeries)
    {
        if (samples.Count <= targetRenderSamplesPerSeries)
        {
            return samples is SessionMetricSample[] array
                ? array
                : samples.ToArray();
        }

        var bucketCount = Math.Max(1, targetRenderSamplesPerSeries / 2);
        var selectedIndices = new SortedSet<int>
        {
            0,
            samples.Count - 1
        };

        for (var bucketIndex = 0; bucketIndex < bucketCount; bucketIndex++)
        {
            var startIndex = bucketIndex * samples.Count / bucketCount;
            var endExclusive = (bucketIndex + 1) * samples.Count / bucketCount;
            if (endExclusive <= startIndex)
            {
                continue;
            }

            var minIndex = startIndex;
            var maxIndex = startIndex;
            for (var sampleIndex = startIndex + 1; sampleIndex < endExclusive; sampleIndex++)
            {
                var sample = samples[sampleIndex];
                if (sample.Value < samples[minIndex].Value)
                {
                    minIndex = sampleIndex;
                }

                if (sample.Value > samples[maxIndex].Value)
                {
                    maxIndex = sampleIndex;
                }
            }

            selectedIndices.Add(minIndex);
            selectedIndices.Add(maxIndex);
        }

        var reducedSamples = new SessionMetricSample[selectedIndices.Count];
        var reducedIndex = 0;
        foreach (var selectedIndex in selectedIndices)
        {
            reducedSamples[reducedIndex++] = samples[selectedIndex];
        }

        return reducedSamples;
    }

    private static MetricChartPreparedSegment[] CreateSegments(IReadOnlyList<SessionMetricSample> samples)
    {
        if (samples.Count == 0)
        {
            return Array.Empty<MetricChartPreparedSegment>();
        }

        return samples
            .GroupBy(sample => Math.Max(0, sample.SegmentId))
            .OrderBy(group => group.Min(sample => sample.CapturedAtUtc))
            .ThenBy(group => group.Key)
            .Select(group => new MetricChartPreparedSegment(
                group.OrderBy(sample => sample.CapturedAtUtc)
                    .ToArray()))
            .ToArray();
    }

    private static MetricChartPreparedSegment[] ReduceSegments(
        IReadOnlyList<MetricChartPreparedSegment> segments,
        int targetRenderSamplesPerSeries)
    {
        if (segments.Count == 0)
        {
            return Array.Empty<MetricChartPreparedSegment>();
        }

        if (segments.Count == 1)
        {
            return
            [
                new MetricChartPreparedSegment(
                    ReduceSamples(segments[0].Samples, targetRenderSamplesPerSeries))
            ];
        }

        var totalSampleCount = segments.Sum(segment => segment.Samples.Length);
        return segments
            .Select(segment =>
            {
                var minimumSamples = segment.Samples.Length <= 1 ? 1 : 2;
                var proportionalTarget = (int)Math.Round(targetRenderSamplesPerSeries * (double)segment.Samples.Length / totalSampleCount);
                var segmentTarget = Math.Max(minimumSamples, proportionalTarget);
                return new MetricChartPreparedSegment(
                    ReduceSamples(segment.Samples, segmentTarget));
            })
            .ToArray();
    }

    private static SessionMetricSample[] FlattenSegments(IReadOnlyList<MetricChartPreparedSegment> segments)
    {
        if (segments.Count == 0)
        {
            return Array.Empty<SessionMetricSample>();
        }

        return segments
            .SelectMany(segment => segment.Samples)
            .ToArray();
    }
}
