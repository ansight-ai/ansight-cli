namespace Ansight.Host.Pairing;

internal enum RemoteToolPolicy
{
    Read = 0,
    Write = 1,
    Critical = 2
}

internal static class PairingToolPolicy
{
    public static string Normalize(string? value, string fallback = "read")
        => TryParse(value, out var policy)
            ? ToWireName(policy)
            : TryParse(fallback, out var fallbackPolicy)
                ? ToWireName(fallbackPolicy)
                : "read";

    public static bool Allows(string? maximumPolicy, string? requiredPolicy)
        => TryParse(maximumPolicy, out var maximum)
           && TryParse(requiredPolicy, out var required)
           && required <= maximum;

    public static bool TryParse(string? value, out RemoteToolPolicy policy)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "read":
                policy = RemoteToolPolicy.Read;
                return true;
            case "write":
                policy = RemoteToolPolicy.Write;
                return true;
            case "critical":
            case "delete":
                policy = RemoteToolPolicy.Critical;
                return true;
            default:
                policy = default;
                return false;
        }
    }

    public static string ToWireName(RemoteToolPolicy policy)
        => policy switch
        {
            RemoteToolPolicy.Read => "read",
            RemoteToolPolicy.Write => "write",
            RemoteToolPolicy.Critical => "critical",
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
        };
}
