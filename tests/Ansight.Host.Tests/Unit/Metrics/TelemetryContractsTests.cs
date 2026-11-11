namespace Ansight.Host.Tests.Unit.Metrics;

public sealed class TelemetryContractsTests
{
    [Fact]
    public void MetricGroups_SurviveWireParsingPersistenceAndEvidencePayloads()
    {
        var parsed = ClientEventParser.ParseText("""
            {"type":"CLIENT_METRIC_CHANNELS","channels":[
              {"id":64,"name":"Latency","group":"Old label","metricGroup":{"id":"checkout","name":"Checkout"}},
              {"id":65,"name":"Legacy","group":"Legacy group"},
              {"id":66,"name":"Invalid group","group":"Fallback","metricGroup":{"id":3,"name":"Invalid"}}
            ]}
            """);
        var channel = Assert.Single(parsed.MetricChannels, value => value.ChannelId == 64);
        Assert.Equal("checkout", channel.MetricGroup?.Id);
        Assert.Equal("Checkout", channel.Group);
        Assert.Null(parsed.MetricChannels.Single(value => value.ChannelId == 65).MetricGroup);
        Assert.Null(parsed.MetricChannels.Single(value => value.ChannelId == 66).MetricGroup);
        Assert.Equal("Fallback", parsed.MetricChannels.Single(value => value.ChannelId == 66).Group);

        var saved = JsonSerializer.Serialize(channel, JsonUtil.Compact);
        var restored = JsonSerializer.Deserialize<SessionMetricChannel>(saved, JsonUtil.Compact)!;
        Assert.Equal(channel.MetricGroup, restored.MetricGroup);
        var payload = SessionEvidencePayloads.BuildMetricChannelPayload(restored);
        Assert.Equal("checkout", payload["metricGroup"]?["id"]?.GetValue<string>());
        Assert.Equal("Checkout", payload["metricGroup"]?["name"]?.GetValue<string>());
    }

    [Fact]
    public void NumericSamples_PreserveFractionsAndRejectNonFiniteValues()
    {
        var parsed = ClientEventParser.ParseText("""
            {"type":"CLIENT_METRICS","metrics":[
              {"channel":64,"value":16.67,"capturedAtUtc":"2026-10-18T00:00:00Z"},
              {"channel":64,"value":-0.125,"capturedAtUtc":"2026-10-18T00:00:01Z"},
              {"channel":64,"value":"NaN"},{"channel":64,"value":"Infinity"},
              {"channel":64,"value":1e999},{"channel":64,"value":7}]}
            """);
        Assert.Equal(new[] { 16.67, -0.125, 7d }, parsed.Metrics.Select(metric => metric.Value));

        var roundTrip = JsonSerializer.Deserialize<SessionMetricSample[]>(JsonSerializer.Serialize(parsed.Metrics))!;
        Assert.Equal(parsed.Metrics.Select(metric => metric.Value), roundTrip.Select(metric => metric.Value));

        var stats = new TelemetryTimelineChannelStats(64);
        stats.Add(0.125);
        stats.Add(0.375);

        var payload = stats.ToPayload(new Dictionary<byte, SessionMetricChannel>
        {
            [64] = new() { ChannelId = 64, Name = "Frame", ColorHex = "#123456", Unit = "ms" }
        });
        Assert.Equal(0.25, payload["average"]!.GetValue<double>());
        Assert.Equal("ms", payload["unit"]!.GetValue<string>());
    }

    [Fact]
    public void Spans_ReconstructOutOfOrderNestedAndIncompleteRecords()
    {
        var start = DateTimeOffset.Parse("2026-10-18T00:00:00Z");

        SessionApplicationEvent Event(string id, string phase, double? elapsed = null, string? parent = null) =>
            new(Guid.NewGuid().ToString(), id, "Span", JsonSerializer.Serialize(new
            {
                spanId = id,
                parentSpanId = parent,
                phase,
                startedAtUtc = start,
                durationMs = elapsed,
                status = phase == "end" ? "ok" : null
            }), start, 64);

        var spans = SessionSpan.FromEvents(new[]
        {
            Event("outer", "end", 16.67),
            Event("outer", "start"),
            Event("child", "start", parent: "outer"),
            Event("child", "end", 0.125, "outer"),
            Event("open", "start"),
            Event("outer", "end", 99),
            Event("invalid", "end", -1),
            new SessionApplicationEvent("bad", "bad", "Span", "{", start, 64)
        });
        Assert.Equal(3, spans.Count);
        Assert.Equal(16.67, spans.Single(span => span.SpanId == "outer").DurationMs);
        Assert.Equal("outer", spans.Single(span => span.SpanId == "child").ParentSpanId);
        Assert.Equal("incomplete", spans.Single(span => span.SpanId == "open").Status);
        Assert.Null(spans.Single(span => span.SpanId == "open").DurationMs);
    }
}
