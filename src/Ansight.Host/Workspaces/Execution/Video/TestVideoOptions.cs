namespace Ansight.Host.Workspaces.Execution;

public sealed record TestVideoOptions(string Retention = "always")
{
    public bool IsEnabled => Retention != "off";

    internal static TestVideoOptions Read(JsonElement root)
    {
        if (!root.TryGetProperty("video", out var video)) return new();
        if (video.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("video must be an object with retention: off, always, or on-failure.");
        foreach (var property in video.EnumerateObject())
            if (property.Name != "retention")
                throw new InvalidDataException($"Unknown video option '{property.Name}'. V1 supports retention only.");
        if (!video.TryGetProperty("retention", out var retention)
            || retention.ValueKind != JsonValueKind.String
            || retention.GetString() is not ("off" or "always" or "on-failure"))
            throw new InvalidDataException("video.retention must be off, always, or on-failure.");
        return new(retention.GetString()!);
    }
}
