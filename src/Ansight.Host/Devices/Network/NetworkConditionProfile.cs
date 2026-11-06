using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Host.Devices.Network;

internal sealed record NetworkConditionProfile
{
    public int UploadKbps { get; init; }
    public int DownloadKbps { get; init; }
    public int LatencyMs { get; init; }
    public int JitterMs { get; init; }
    public int Seed { get; init; }
    public bool Stall { get; init; }
    public bool Blocked { get; init; }
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public static NetworkConditionProfile Read(JsonNode? value)
    {
        var profile = value?.Deserialize<NetworkConditionProfile>(JsonOptions)
            ?? throw new ArgumentException("profile is required.");

        if (profile.UploadKbps is < 0 or > 1_000_000 || profile.DownloadKbps is < 0 or > 1_000_000
            || profile.LatencyMs is < 0 or > 10_000 || profile.JitterMs is < 0 or > 10_000)
        {
            throw new ArgumentException("Rates must be 0–1,000,000 kbps (0 = unlimited); delay and jitter must be 0–10,000 ms.");
        }

        if (profile.Stall && profile.Blocked)
        {
            throw new ArgumentException("Choose stall or blocked, not both.");
        }

        return profile;
    }
}
