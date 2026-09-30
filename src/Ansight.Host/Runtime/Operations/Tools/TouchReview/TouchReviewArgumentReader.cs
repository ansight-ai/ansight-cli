using System.Globalization;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal static class TouchReviewArgumentReader
{
    public static bool TryReadTouchFilters(JsonObject? arguments, out TouchFilters filters, out string? errorMessage)
    {
        filters = TouchFilters.Empty;
        errorMessage = null;
        if (!ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "startUtc", out var startUtc, out errorMessage)
            || !ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "endUtc", out var endUtc, out errorMessage)
            || !TryReadOptionalIntegerArgument(arguments, "minPointerCount", out var minPointerCount, out errorMessage)
            || !TryReadOptionalIntegerArgument(arguments, "maxPointerCount", out var maxPointerCount, out errorMessage))
        {
            return false;
        }

        if (startUtc.HasValue && endUtc.HasValue && startUtc.Value > endUtc.Value)
        {
            errorMessage = "startUtc must be before or equal to endUtc.";
            return false;
        }

        if (minPointerCount.HasValue && minPointerCount.Value < 1)
        {
            errorMessage = "minPointerCount must be greater than 0.";
            return false;
        }

        if (maxPointerCount.HasValue && maxPointerCount.Value < 1)
        {
            errorMessage = "maxPointerCount must be greater than 0.";
            return false;
        }

        if (minPointerCount.HasValue && maxPointerCount.HasValue && minPointerCount.Value > maxPointerCount.Value)
        {
            errorMessage = "minPointerCount must be less than or equal to maxPointerCount.";
            return false;
        }

        filters = new TouchFilters(
            startUtc?.ToUniversalTime(),
            endUtc?.ToUniversalTime(),
            ReadTouchActionFilters(arguments),
            ReadLongSet(arguments, "pointerIds"),
            minPointerCount,
            maxPointerCount);
        return true;
    }

    public static bool TryReadOptionalIntegerArgument(JsonObject? arguments, string propertyName, out int? value, out string? errorMessage)
    {
        value = null;
        errorMessage = null;
        if (arguments?[propertyName] is null)
        {
            return true;
        }

        if (arguments[propertyName] is JsonValue jsonValue
            && (jsonValue.TryGetValue<int>(out var parsedValue)
                || int.TryParse(jsonValue.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedValue)))
        {
            value = parsedValue;
            return true;
        }

        errorMessage = $"'{propertyName}' must be an integer.";
        return false;
    }

    public static bool TryReadBooleanArgument(
        JsonObject? arguments,
        string propertyName,
        bool defaultValue,
        out bool value,
        out string? errorMessage)
    {
        value = defaultValue;
        errorMessage = null;
        if (arguments?[propertyName] is null)
        {
            return true;
        }

        if (arguments[propertyName] is JsonValue jsonValue
            && (jsonValue.TryGetValue<bool>(out var parsedValue)
                || bool.TryParse(jsonValue.ToString(), out parsedValue)))
        {
            value = parsedValue;
            return true;
        }

        errorMessage = $"'{propertyName}' must be true or false.";
        return false;
    }

    public static bool TryReadBoundedPositiveInteger(
        JsonObject? arguments,
        string propertyName,
        int defaultValue,
        int maxValue,
        out int value,
        out string? errorMessage)
    {
        return ArgumentReader.TryReadPositiveLimit(arguments, propertyName, defaultValue, maxValue, out value, out errorMessage);
    }

    public static bool TryReadBoundedNonNegativeInteger(
        JsonObject? arguments,
        string propertyName,
        int defaultValue,
        int maxValue,
        out int value,
        out string? errorMessage)
    {
        value = defaultValue;
        errorMessage = null;
        if (arguments?[propertyName] is null)
        {
            return true;
        }

        if (arguments[propertyName] is JsonValue jsonValue
            && (jsonValue.TryGetValue<int>(out var parsedValue)
                || int.TryParse(jsonValue.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedValue)))
        {
            if (parsedValue < 0)
            {
                errorMessage = $"'{propertyName}' must be greater than or equal to 0.";
                return false;
            }

            value = Math.Min(parsedValue, maxValue);
            return true;
        }

        errorMessage = $"'{propertyName}' must be an integer.";
        return false;
    }

    public static bool TryReadExportFormat(JsonObject? arguments, out string format, out string? errorMessage)
    {
        format = "both";
        errorMessage = null;
        var rawFormat = NormalizeOptionalString(arguments?["format"]?.GetValue<string>());
        if (rawFormat is null)
        {
            return true;
        }

        if (string.Equals(rawFormat, "json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rawFormat, "markdown", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rawFormat, "both", StringComparison.OrdinalIgnoreCase))
        {
            format = rawFormat.ToLowerInvariant();
            return true;
        }

        errorMessage = "format must be one of: json, markdown, both.";
        return false;
    }

    private static HashSet<string> ReadTouchActionFilters(JsonObject? arguments)
    {
        var actions = ArgumentReader.ReadStringSet(arguments, "actions");
        return actions
            .Select(TouchReviewGeometry.NormalizeTouchAction)
            .Where(action => !string.IsNullOrWhiteSpace(action))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<long> ReadLongSet(JsonObject? arguments, string propertyName)
    {
        var values = new HashSet<long>();
        if (arguments?[propertyName] is not JsonArray array)
        {
            return values;
        }

        foreach (var item in array)
        {
            if (item is JsonValue jsonValue
                && (jsonValue.TryGetValue<long>(out var longValue)
                    || long.TryParse(jsonValue.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out longValue)))
            {
                values.Add(longValue);
            }
        }

        return values;
    }

    private static string? NormalizeOptionalString(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
