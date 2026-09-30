namespace Ansight.Host.Models.Metrics;

public static class MetricChannelClassification
{
    private const byte FramesPerSecondChannelId = 3;
    private const string FramesPerSecondChannelName = "FPS";

    private static readonly HashSet<string> MemoryChannelNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".NET",
        "CLR memory",
        "Managed heap",
        "Java heap",
        "Native heap",
        "Physical Footprint",
        "Physical footprint",
        "Resident set size",
        "Resident Set Size",
        "RSS"
    };

    public static bool IsFpsChannel(IReadOnlyDictionary<byte, SessionMetricChannel> channels, byte channelId)
    {
        if (!channels.TryGetValue(channelId, out var channel))
        {
            return channelId == FramesPerSecondChannelId;
        }

        var hasExplicitMetadata = !string.IsNullOrWhiteSpace(channel.Unit)
            || (!string.IsNullOrWhiteSpace(channel.Type) && !string.Equals(channel.Type, "custom", StringComparison.OrdinalIgnoreCase));
        return string.Equals(channel.Type, "frames", StringComparison.OrdinalIgnoreCase)
               || string.Equals(channel.Type, "fps", StringComparison.OrdinalIgnoreCase)
               || string.Equals(channel.Unit, "fps", StringComparison.OrdinalIgnoreCase)
               || (!hasExplicitMetadata && (string.Equals(channel.Name, FramesPerSecondChannelName, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(channel.Name, "Rendered frames per second", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(channel.Kind, "rendered-fps", StringComparison.OrdinalIgnoreCase)
                   || (string.IsNullOrWhiteSpace(channel.Name) && string.IsNullOrWhiteSpace(channel.Kind)
                       && channelId == FramesPerSecondChannelId)));
    }

    public static bool IsMemoryChannel(IReadOnlyDictionary<byte, SessionMetricChannel> channels, byte channelId)
    {
        if (!channels.TryGetValue(channelId, out var channel))
        {
            return channelId is 0 or 1 or 2;
        }

        if (string.Equals(channel.Type, "memory", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var name = channel.Name.Trim();
        if (IsReactNativeChannel(channel)
            && (ContainsReactNativeMemoryHint(channel.Kind) || ContainsReactNativeMemoryHint(name)))
        {
            return true;
        }

        return MemoryChannelNames.Contains(name)
               || name.Contains("memory", StringComparison.OrdinalIgnoreCase)
               || name.Contains("heap", StringComparison.OrdinalIgnoreCase)
               || name.Contains("footprint", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "rss", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsReactNativeChannel(SessionMetricChannel? channel)
    {
        if (channel is null)
        {
            return false;
        }

        return string.Equals(channel.Source, "reactNative", StringComparison.OrdinalIgnoreCase)
               || string.Equals(channel.Group, "React Native", StringComparison.OrdinalIgnoreCase)
               || (channel.Kind?.StartsWith("react_native_", StringComparison.OrdinalIgnoreCase) ?? false)
               || channel.Name.Contains("React Native", StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolveTelemetryType(byte channelId, IReadOnlyDictionary<byte, SessionMetricChannel> channels)
    {
        channels.TryGetValue(channelId, out var channel);
        return ResolveTelemetryType(channelId, channel);
    }

    public static string ResolveTelemetryType(byte channelId, SessionMetricChannel? channel)
    {
        var channels = channel is null
            ? new Dictionary<byte, SessionMetricChannel>()
            : new Dictionary<byte, SessionMetricChannel> { [channelId] = channel };

        if (IsFpsChannel(channels, channelId))
        {
            return "fps";
        }

        if (IsMemoryChannel(channels, channelId))
        {
            return "memory";
        }

        if (!string.IsNullOrWhiteSpace(channel?.Type) && !string.Equals(channel.Type, "custom", StringComparison.OrdinalIgnoreCase))
        {
            return FileNameUtil.Sanitize(channel.Type.Trim()).Replace('_', '-');
        }

        if (string.IsNullOrWhiteSpace(channel?.Name))
        {
            return $"channel-{channelId}";
        }

        return FileNameUtil.Sanitize(channel.Name).Replace('_', '-');
    }

    private static bool ContainsReactNativeMemoryHint(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && (value.Contains("memory", StringComparison.OrdinalIgnoreCase)
                   || value.Contains("heap", StringComparison.OrdinalIgnoreCase));
    }
}
