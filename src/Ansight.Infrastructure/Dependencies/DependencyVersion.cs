using System.Globalization;
using System.Text.RegularExpressions;

namespace Ansight.Infrastructure.Dependencies;

internal sealed partial class DependencyVersion(int[] parts, string[] prerelease) : IComparable<DependencyVersion>
{
    private readonly int[] parts = parts;
    private readonly string[] prerelease = prerelease;

    internal static DependencyVersion Parse(string value)
        => TryParse(value, out var version) ? version! : throw new FormatException($"Invalid dependency version: {value}");

    internal static bool TryParse(string? value, out DependencyVersion? version)
    {
        version = null;
        if (value is null) return false;
        var match = Pattern().Match(value);
        if (!match.Success) return false;
        var parts = new int[4];
        var components = match.Groups[1].Value.Split('.');
        for (var index = 0; index < components.Length; index++)
            if (!int.TryParse(components[index], NumberStyles.None, CultureInfo.InvariantCulture, out parts[index])) return false;
        version = new(parts, match.Groups[2].Success ? match.Groups[2].Value.Split('.') : []);
        return true;
    }

    public int CompareTo(DependencyVersion? other)
    {
        if (other is null) return 1;
        for (var index = 0; index < parts.Length; index++)
        {
            var comparison = parts[index].CompareTo(other.parts[index]);
            if (comparison != 0) return comparison;
        }
        if (prerelease.Length == 0 || other.prerelease.Length == 0)
            return (prerelease.Length == 0 ? 1 : 0).CompareTo(other.prerelease.Length == 0 ? 1 : 0);
        for (var index = 0; index < Math.Min(prerelease.Length, other.prerelease.Length); index++)
        {
            var left = prerelease[index];
            var right = other.prerelease[index];
            var leftNumeric = left.All(char.IsAsciiDigit);
            var rightNumeric = right.All(char.IsAsciiDigit);
            var comparison = leftNumeric && rightNumeric
                ? CompareNumericIdentifier(left, right)
                : leftNumeric != rightNumeric ? (leftNumeric ? -1 : 1)
                : string.CompareOrdinal(left, right);
            if (comparison != 0) return comparison;
        }
        return prerelease.Length.CompareTo(other.prerelease.Length);
    }

    private static int CompareNumericIdentifier(string left, string right)
    {
        left = left.TrimStart('0');
        right = right.TrimStart('0');
        var length = left.Length.CompareTo(right.Length);
        return length != 0 ? length : string.CompareOrdinal(left, right);
    }

    [GeneratedRegex(@"\A(\d+(?:\.\d+){1,3})(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
