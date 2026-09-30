using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.SessionReview;

internal static class SessionPlatformFilters
{
    public static HashSet<string> Read(JsonObject? arguments)
    {
        var filters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddFilter(filters, arguments?["platform"]?.GetValue<string>());
        AddFilter(filters, arguments?["platformKey"]?.GetValue<string>());
        AddFilters(filters, arguments?["platforms"] as JsonArray);
        AddFilters(filters, arguments?["platformKeys"] as JsonArray);
        return filters;
    }

    public static string ResolveSessionPlatformKey(AppSessionSnapshot snapshot)
    {
        foreach (var candidate in EnumerateSessionPlatformCandidates(snapshot))
        {
            var platformKey = ResolvePlatformKey(candidate);
            if (!string.Equals(platformKey, "other", StringComparison.OrdinalIgnoreCase))
            {
                return platformKey;
            }
        }

        return "other";
    }

    public static string ResolveSessionOperatingSystem(AppSessionSnapshot snapshot)
    {
        return string.IsNullOrWhiteSpace(snapshot.DeviceProfile?.Device?.OsName)
            ? "Unknown"
            : snapshot.DeviceProfile.Device.OsName.Trim();
    }

    private static void AddFilters(HashSet<string> filters, JsonArray? array)
    {
        if (array is null)
        {
            return;
        }

        foreach (var item in array)
        {
            AddFilter(filters, item?.GetValue<string>());
        }
    }

    private static void AddFilter(HashSet<string> filters, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (string.Equals(value.Trim(), "apple", StringComparison.OrdinalIgnoreCase))
        {
            filters.Add("ios");
            filters.Add("macos");
            return;
        }

        filters.Add(ResolvePlatformKey(value));
    }

    private static IEnumerable<string?> EnumerateSessionPlatformCandidates(AppSessionSnapshot snapshot)
    {
        yield return snapshot.DeviceProfile?.Device?.OsName;

        if (snapshot.DeviceProfile?.Runtime?.Stack is { Count: > 0 } runtimeStack)
        {
            foreach (var entry in runtimeStack)
            {
                yield return entry.Name;
            }
        }

        yield return snapshot.DeviceProfile?.Runtime?.Engine?.Name;
    }

    private static string ResolvePlatformKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "other";
        }

        var normalized = value.Trim();
        if (string.Equals(normalized, "android", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("android", StringComparison.OrdinalIgnoreCase))
        {
            return "android";
        }

        if (string.Equals(normalized, "ios", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("ios", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("iphone", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("ipad", StringComparison.OrdinalIgnoreCase))
        {
            return "ios";
        }

        if (string.Equals(normalized, "windows", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("windows", StringComparison.OrdinalIgnoreCase))
        {
            return "windows";
        }

        if (string.Equals(normalized, "macos", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("mac", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("catalyst", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("os x", StringComparison.OrdinalIgnoreCase))
        {
            return "macos";
        }

        return "other";
    }
}
