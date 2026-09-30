namespace Ansight.Host.Utilities;

using System.Text.Json.Nodes;
using Ansight.Pairing.Models;

internal static class SessionSdkVersion
{
    public static string? Resolve(string? sdkVersion, DeviceAppProfile? profile, string? profileJson)
    {
        return Normalize(sdkVersion)
               ?? Resolve(profile, profileJson);
    }

    public static string? Resolve(DeviceAppProfile? profile, string? profileJson)
    {
        return Normalize(profile?.Sdk?.Version)
               ?? ResolveFromJson(profileJson);
    }

    private static string? ResolveFromJson(string? profileJson)
    {
        if (string.IsNullOrWhiteSpace(profileJson))
        {
            return null;
        }

        try
        {
            var root = JsonNode.Parse(profileJson) as JsonObject;
            if (root is null)
            {
                return null;
            }

            return ReadString(root["sdk"]?["version"])
                   ?? ReadString(root["sdkVersion"]);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text)
            ? Normalize(text)
            : null;
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }
}
