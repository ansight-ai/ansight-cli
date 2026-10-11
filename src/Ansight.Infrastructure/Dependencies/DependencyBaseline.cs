using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansight.Infrastructure.Dependencies;

public sealed record DependencyVersionPolicy(string Min, string Target, string? MaxExclusive = null);

public sealed record DependencyDefinition(
    string Id, DependencyVersionPolicy Versions, IReadOnlyList<string> Platforms,
    string UsedBy, IReadOnlyDictionary<string, string> Providers, string? Basis = null);

public sealed record DependencyBaselineManifest(
    int SchemaVersion, string RecordedOn, string RecordedPlatform, string Basis, string Notes,
    IReadOnlyList<DependencyDefinition> Dependencies);

public sealed record DependencyBaselineSnapshot(
    string Status, string? Sha256, DependencyBaselineManifest? Manifest);

public sealed record DependencyVersionAdvice(
    string DependencyId, string? DetectedVersion, DependencyVersionPolicy Versions,
    string Status, string? Notice);

/// <summary>The release's embedded setup baseline. Version comparisons are informational only.</summary>
public static class DependencyBaseline
{
    private const string ResourceName = "Ansight.DependencyBaseline.json";
    private static readonly Lazy<DependencyBaselineSnapshot> bundled = new(LoadBundled);

    public static DependencyBaselineSnapshot Bundled => bundled.Value;

    public static DependencyVersionAdvice? Compare(string id, string? detectedVersion)
    {
        var definition = Bundled.Manifest?.Dependencies.FirstOrDefault(item => item.Id == id);
        return definition is null ? null : Compare(definition, detectedVersion);
    }

    public static DependencyVersionAdvice Compare(DependencyDefinition definition, string? detectedVersion)
    {
        var policy = definition.Versions;
        var version = detectedVersion;
        // Android's package build number is not a SemVer prerelease identifier.
        if (definition.Id is "adb" or "emulator" && version is not null)
        {
            var separator = version.IndexOf('-');
            if (separator >= 0 && version[(separator + 1)..].Length > 0
                && version[(separator + 1)..].All(char.IsAsciiDigit))
                version = version[..separator];
        }

        if (!DependencyVersion.TryParse(version, out var installed))
            return new(definition.Id, detectedVersion, policy, "unknown",
                $"{definition.Id}: the installed version could not be compared. This Ansight release was set up against {policy.Target}; this is informational only.");
        var minimum = DependencyVersion.Parse(policy.Min);
        var target = DependencyVersion.Parse(policy.Target);
        var maximum = policy.MaxExclusive is null ? null : DependencyVersion.Parse(policy.MaxExclusive);
        var status = installed!.CompareTo(minimum) < 0 ? "below-minimum"
            : maximum is not null && installed.CompareTo(maximum) >= 0 ? "above-maximum"
            : installed.CompareTo(target) == 0 ? "at-target" : "within-range";
        var range = policy.MaxExclusive is null ? $">={policy.Min}" : $">={policy.Min} and <{policy.MaxExclusive}";
        var notice = status is "below-minimum" or "above-maximum"
            ? $"{definition.Id} {detectedVersion} differs from this release's expected range ({range}). Ansight was set up against {policy.Target} and will likely still work; if you encounter issues, try that version."
            : null;
        return new(definition.Id, detectedVersion, policy, status, notice);
    }

    private static DependencyBaselineSnapshot LoadBundled()
    {
        try
        {
            using var stream = typeof(DependencyBaseline).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null) return new("unavailable", null, null);
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            var content = bytes.ToArray();
            var manifest = JsonSerializer.Deserialize<DependencyBaselineManifest>(content,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow });
            if (manifest?.SchemaVersion != 1 || manifest.Dependencies is null
                || manifest.Dependencies.Any(item => !HasValidPolicy(item)))
                return new("unavailable", null, null);
            return new("embedded", Convert.ToHexStringLower(SHA256.HashData(content)), manifest);
        }
        catch (Exception exception) when (exception is JsonException or IOException or NotSupportedException)
        {
            // Metadata must never prevent the application from starting or change readiness results.
            return new("unavailable", null, null);
        }
    }

    private static bool HasValidPolicy(DependencyDefinition? definition)
    {
        if (definition?.Versions is not { } policy
            || !DependencyVersion.TryParse(policy.Min, out var minimum)
            || !DependencyVersion.TryParse(policy.Target, out var target)
            || minimum!.CompareTo(target) > 0) return false;
        return policy.MaxExclusive is null
            || (DependencyVersion.TryParse(policy.MaxExclusive, out var maximum) && target!.CompareTo(maximum) < 0);
    }
}
