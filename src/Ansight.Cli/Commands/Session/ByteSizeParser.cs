using System.Globalization;

namespace Ansight.Cli.Commands.Session;

internal static class ByteSizeParser
{
    private static readonly IReadOnlyDictionary<string, decimal> Multipliers =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            [""] = 1m,
            ["b"] = 1m,
            ["kb"] = 1_000m,
            ["kib"] = 1_024m,
            ["mb"] = 1_000_000m,
            ["mib"] = 1_048_576m,
            ["gb"] = 1_000_000_000m,
            ["gib"] = 1_073_741_824m,
            ["tb"] = 1_000_000_000_000m,
            ["tib"] = 1_099_511_627_776m
        };

    public static long Parse(string source, string optionName, long minimum, long maximum)
    {
        var normalized = source.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
        var unitStart = 0;
        while (unitStart < normalized.Length
               && (char.IsDigit(normalized[unitStart]) || normalized[unitStart] is '.' or ','))
        {
            unitStart++;
        }

        var numericSource = normalized[..unitStart];
        var unit = normalized[unitStart..];
        if (!decimal.TryParse(
                numericSource,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var numericValue)
            || numericValue < 0
            || !Multipliers.TryGetValue(unit, out var multiplier))
        {
            throw InvalidValue(optionName, minimum, maximum);
        }

        var byteValue = decimal.Ceiling(numericValue * multiplier);
        if (byteValue < minimum || byteValue > maximum || byteValue > long.MaxValue)
        {
            throw InvalidValue(optionName, minimum, maximum);
        }

        return decimal.ToInt64(byteValue);
    }

    private static CliUsageException InvalidValue(string optionName, long minimum, long maximum)
    {
        return new CliUsageException(
            $"--{optionName} must be a byte size between {Format(minimum)} and {Format(maximum)} "
            + "using B, KB, KiB, MB, MiB, GB, GiB, TB, or TiB.");
    }

    private static string Format(long bytes)
    {
        const decimal gibibyte = 1024m * 1024m * 1024m;
        return $"{bytes / gibibyte:0.##} GiB";
    }
}
