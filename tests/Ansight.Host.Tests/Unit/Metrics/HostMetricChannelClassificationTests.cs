using Ansight.Host.Runtime.Operations;

namespace Ansight.Host.Tests.Unit.Metrics;

public sealed class HostMetricChannelClassificationTests
{
    [Fact]
    public void IncompleteExternalCpuMetadata_DoesNotInheritLegacyFpsIdentity()
    {
        var channels = new Dictionary<byte, SessionMetricChannel>
        {
            [3] = Channel(3, "Process CPU", kind: "cpu-millicores"),
            [4] = Channel(4, "Rendered frames per second")
        };

        Assert.False(MetricChannelClassification.IsFpsChannel(channels, 3));
        Assert.NotEqual("fps", PayloadJson.ResolveTelemetryType(3, channels));
        Assert.True(MetricChannelClassification.IsFpsChannel(channels, 4));
        Assert.True(MetricChannelClassification.IsFpsChannel(new Dictionary<byte, SessionMetricChannel> { [3] = Channel(3, "FPS") }, 3));
    }

    [Fact]
    public void ExternalCpuAndFps_UseMetadataInsteadOfLegacyChannelNumbers()
    {
        var channels = new Dictionary<byte, SessionMetricChannel>
        {
            [3] = Channel(3, "Process CPU", unit: "millicores", type: "cpu"),
            [4] = Channel(4, "Rendered frames per second", unit: "fps", type: "fps")
        };

        Assert.False(MetricChannelClassification.IsFpsChannel(channels, 3));
        Assert.False(MetricChannelClassification.IsMemoryChannel(channels, 3));
        Assert.Equal("cpu", PayloadJson.ResolveTelemetryType(3, channels));
        Assert.True(MetricChannelClassification.IsFpsChannel(channels, 4));
        Assert.Equal("fps", PayloadJson.ResolveTelemetryType(4, channels));
        Assert.True(MetricChannelClassification.IsFpsChannel(new Dictionary<byte, SessionMetricChannel>(), 3));
    }

    [Theory]
    [InlineData(0, ".NET")]
    [InlineData(0, "Managed heap")]
    [InlineData(0, "Java heap")]
    [InlineData(1, "Native heap")]
    [InlineData(1, "Physical Footprint")]
    [InlineData(2, "RSS")]
    [InlineData(2, "Resident set size")]
    public void ResolveTelemetryType_RecognizesSdkMemoryChannelNames(byte channelId, string name)
    {
        var channels = new Dictionary<byte, SessionMetricChannel>
        {
            [channelId] = Channel(channelId, name, unit: "bytes")
        };

        Assert.True(MetricChannelClassification.IsMemoryChannel(channels, channelId));
        Assert.Equal("memory", PayloadJson.ResolveTelemetryType(channelId, channels));
    }

    [Fact]
    public void ResolveTelemetryType_UsesChannelTypeMetadata()
    {
        var channels = new Dictionary<byte, SessionMetricChannel>
        {
            [42] = Channel(42, "Hermes heap", unit: "bytes", type: "memory"),
            [43] = Channel(43, "Battery Level", unit: "percent", type: "battery")
        };

        Assert.Equal("memory", PayloadJson.ResolveTelemetryType(42, channels));
        Assert.Equal("battery", PayloadJson.ResolveTelemetryType(43, channels));
    }

    [Fact]
    public void ResolveTelemetryType_RecognizesReactNativeHeapMetadata()
    {
        var channels = new Dictionary<byte, SessionMetricChannel>
        {
            [32] = Channel(
                32,
                "JS used",
                unit: "bytes",
                source: "reactNative",
                group: "React Native",
                kind: "react_native_js_heap_used")
        };

        Assert.True(MetricChannelClassification.IsReactNativeChannel(channels[32]));
        Assert.True(MetricChannelClassification.IsMemoryChannel(channels, 32));
        Assert.Equal("memory", PayloadJson.ResolveTelemetryType(32, channels));
    }

    [Fact]
    public void IsFpsChannel_UsesChannelTypeAndUnitMetadata()
    {
        var channels = new Dictionary<byte, SessionMetricChannel>
        {
            [42] = Channel(42, "React Native JS FPS", unit: "fps", type: "reactNative")
        };

        Assert.True(MetricChannelClassification.IsFpsChannel(channels, 42));
        Assert.Equal("fps", PayloadJson.ResolveTelemetryType(42, channels));
    }

    [Fact]
    public void BuildMetricChannelPayload_IncludesChannelIdentityMetadata()
    {
        var payload = SessionEvidencePayloads.BuildMetricChannelPayload(Channel(
            32,
            "React Native JS heap used",
            unit: "bytes",
            type: "memory",
            source: "reactNative",
            group: "React Native",
            kind: "react_native_js_heap_used"));

        Assert.Equal("reactNative", payload["source"]!.GetValue<string>());
        Assert.Equal("React Native", payload["group"]!.GetValue<string>());
        Assert.Equal("react_native_js_heap_used", payload["kind"]!.GetValue<string>());
        Assert.Equal("memory", payload["type"]!.GetValue<string>());
    }

    private static SessionMetricChannel Channel(
        byte channelId,
        string name,
        string? unit = null,
        string type = "custom",
        string? source = null,
        string? group = null,
        string? kind = null)
    {
        return new SessionMetricChannel
        {
            ChannelId = channelId,
            Name = name,
            ColorHex = "#007AFF",
            Unit = unit,
            Type = type,
            Source = source,
            Group = group,
            Kind = kind
        };
    }
}
