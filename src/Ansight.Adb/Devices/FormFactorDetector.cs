using System.Globalization;
using System.Text.RegularExpressions;
using Ansight.Adb;

namespace Ansight.Adb.Devices;

internal static partial class FormFactorDetector
{
    private const double TabletSmallestWidthDp = 600d;

    public static AndroidDeviceFormFactor FromConnectedDevice(
        string? characteristics,
        string? sizeOutput,
        string? densityOutput)
    {
        var normalizedCharacteristics = characteristics?.Trim() ?? string.Empty;
        if (ContainsAnyToken(normalizedCharacteristics, "watch", "tv", "automotive", "embedded", "desktop"))
        {
            return AndroidDeviceFormFactor.Unknown;
        }

        if (ContainsToken(normalizedCharacteristics, "tablet"))
        {
            return AndroidDeviceFormFactor.Tablet;
        }

        var sizeMatch = PhysicalSizePattern().Match(sizeOutput ?? string.Empty);
        var densityMatch = PhysicalDensityPattern().Match(densityOutput ?? string.Empty);
        if (sizeMatch.Success
            && densityMatch.Success
            && TryParsePositive(sizeMatch.Groups[1].Value, out var widthPixels)
            && TryParsePositive(sizeMatch.Groups[2].Value, out var heightPixels)
            && TryParsePositive(densityMatch.Groups[1].Value, out var densityDpi))
        {
            return FromDisplayMetrics(widthPixels, heightPixels, densityDpi);
        }

        return ContainsToken(normalizedCharacteristics, "phone")
            ? AndroidDeviceFormFactor.Phone
            : AndroidDeviceFormFactor.Unknown;
    }

    public static AndroidDeviceFormFactor FromAvdConfiguration(
        string? deviceName,
        string? tagId,
        string? width,
        string? height,
        string? density)
    {
        var profile = $"{deviceName},{tagId}";
        if (ContainsAnyToken(profile, "wear", "watch", "tv", "automotive", "desktop"))
        {
            return AndroidDeviceFormFactor.Unknown;
        }

        if (TryParsePositive(width, out var widthPixels)
            && TryParsePositive(height, out var heightPixels)
            && TryParsePositive(density, out var densityDpi))
        {
            return FromDisplayMetrics(widthPixels, heightPixels, densityDpi);
        }

        return AndroidDeviceFormFactor.Unknown;
    }

    private static AndroidDeviceFormFactor FromDisplayMetrics(
        int widthPixels,
        int heightPixels,
        int densityDpi)
    {
        var smallestWidthDp = Math.Min(widthPixels, heightPixels) * 160d / densityDpi;
        return smallestWidthDp >= TabletSmallestWidthDp
            ? AndroidDeviceFormFactor.Tablet
            : AndroidDeviceFormFactor.Phone;
    }

    private static bool ContainsAnyToken(string value, params string[] tokens)
        => tokens.Any(token => ContainsToken(value, token));

    private static bool ContainsToken(string value, string token)
        => value.Split(
                [',', ';', ':', '_', '-', ' '],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(token, StringComparer.OrdinalIgnoreCase);

    private static bool TryParsePositive(string? value, out int parsed)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed)
           && parsed > 0;

    [GeneratedRegex(@"Physical size:\s*(\d+)x(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PhysicalSizePattern();

    [GeneratedRegex(@"Physical density:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PhysicalDensityPattern();
}
