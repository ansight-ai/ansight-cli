namespace Ansight.Cli.Commands.Update;

internal sealed class CliReleaseVersion : IComparable<CliReleaseVersion>
{
    private CliReleaseVersion(string source, IReadOnlyList<int> core, IReadOnlyList<string> prerelease)
    {
        Source = source;
        Core = core;
        Prerelease = prerelease;
    }

    public string Source { get; }

    private IReadOnlyList<int> Core { get; }

    private IReadOnlyList<string> Prerelease { get; }

    public static bool TryParse(string? source, out CliReleaseVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        var normalized = source.Trim();
        var metadataSeparator = normalized.IndexOf('+', StringComparison.Ordinal);
        if (metadataSeparator >= 0)
        {
            normalized = normalized[..metadataSeparator];
        }

        var prereleaseSeparator = normalized.IndexOf('-', StringComparison.Ordinal);
        var coreSource = prereleaseSeparator >= 0
            ? normalized[..prereleaseSeparator]
            : normalized;
        var prereleaseSource = prereleaseSeparator >= 0
            ? normalized[(prereleaseSeparator + 1)..]
            : string.Empty;
        var coreParts = coreSource.Split('.', StringSplitOptions.None);
        if (coreParts.Length == 0
            || coreParts.Any(static part => !int.TryParse(part, out var value) || value < 0))
        {
            return false;
        }

        var core = new List<int>(coreParts.Length);
        foreach (var part in coreParts)
        {
            core.Add(int.Parse(part, System.Globalization.CultureInfo.InvariantCulture));
        }

        IReadOnlyList<string> prerelease = string.IsNullOrEmpty(prereleaseSource)
            ? Array.Empty<string>()
            : prereleaseSource.Split('.', StringSplitOptions.None).ToList();
        if (prerelease.Any(static part => string.IsNullOrEmpty(part)
                                          || part.Any(character => !char.IsAsciiLetterOrDigit(character)
                                                                   && character != '-')))
        {
            return false;
        }

        version = new CliReleaseVersion(source.Trim(), core, prerelease);
        return true;
    }

    public int CompareTo(CliReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var coreLength = Math.Max(Core.Count, other.Core.Count);
        for (var index = 0; index < coreLength; index++)
        {
            var left = index < Core.Count ? Core[index] : 0;
            var right = index < other.Core.Count ? other.Core[index] : 0;
            var comparison = left.CompareTo(right);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        if (Prerelease.Count == 0 || other.Prerelease.Count == 0)
        {
            return Prerelease.Count == other.Prerelease.Count
                ? 0
                : Prerelease.Count == 0 ? 1 : -1;
        }

        var prereleaseLength = Math.Max(Prerelease.Count, other.Prerelease.Count);
        for (var index = 0; index < prereleaseLength; index++)
        {
            if (index >= Prerelease.Count)
            {
                return -1;
            }

            if (index >= other.Prerelease.Count)
            {
                return 1;
            }

            var comparison = ComparePrereleaseIdentifier(Prerelease[index], other.Prerelease[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static int ComparePrereleaseIdentifier(string left, string right)
    {
        var leftIsNumeric = int.TryParse(left, out var leftNumber);
        var rightIsNumeric = int.TryParse(right, out var rightNumber);
        if (leftIsNumeric && rightIsNumeric)
        {
            return leftNumber.CompareTo(rightNumber);
        }

        if (leftIsNumeric != rightIsNumeric)
        {
            return leftIsNumeric ? -1 : 1;
        }

        return string.Compare(left, right, StringComparison.Ordinal);
    }
}
