using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations;

internal static class ArgumentReader
{
    public static bool TryReadDateTimeOffsetArgument(JsonObject? arguments, string propertyName, out DateTimeOffset? value, out string? errorMessage)
    {
        errorMessage = null;
        value = null;
        if (arguments?[propertyName] is null)
        {
            return true;
        }

        var text = arguments[propertyName]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (DateTimeOffset.TryParse(text, out var parsed))
        {
            value = parsed;
            return true;
        }

        errorMessage = $"'{propertyName}' must be a valid ISO-8601 timestamp.";
        return false;
    }

    public static bool TryReadPositiveLimit(JsonObject? arguments, string propertyName, int defaultValue, int maxValue, out int limit, out string? errorMessage)
    {
        errorMessage = null;
        limit = defaultValue;
        if (arguments?[propertyName] is null)
        {
            return true;
        }

        if (arguments[propertyName] is JsonValue numericValue
            && (numericValue.TryGetValue<int>(out var parsedLimit) || int.TryParse(numericValue.ToString(), out parsedLimit)))
        {
            if (parsedLimit <= 0)
            {
                errorMessage = $"'{propertyName}' must be greater than 0.";
                return false;
            }

            limit = Math.Min(parsedLimit, maxValue);
            return true;
        }

        errorMessage = $"'{propertyName}' must be an integer.";
        return false;
    }

    public static bool TryReadMinimumVerbosity(JsonObject? arguments, out LogPriority? minimumVerbosity, out string? errorMessage)
    {
        errorMessage = null;
        minimumVerbosity = null;
        var rawValue = arguments?["minimumVerbosity"]?.GetValue<string>()
                       ?? arguments?["minimumPriority"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return true;
        }

        if (Enum.TryParse<LogPriority>(rawValue, ignoreCase: true, out var parsed)
            && parsed != LogPriority.Unknown)
        {
            minimumVerbosity = parsed;
            return true;
        }

        errorMessage = "minimumVerbosity must be one of: verbose, debug, information, warning, error, fatal.";
        return false;
    }

    public static bool TryReadOptionalBooleanArgument(JsonObject? arguments, string propertyName, out bool? value, out string? errorMessage)
    {
        errorMessage = null;
        value = null;
        if (arguments?[propertyName] is null)
        {
            return true;
        }

        if (arguments[propertyName] is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<bool>(out var boolValue))
            {
                value = boolValue;
                return true;
            }

            if (bool.TryParse(jsonValue.ToString(), out boolValue))
            {
                value = boolValue;
                return true;
            }
        }

        errorMessage = $"'{propertyName}' must be true or false.";
        return false;
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
                || int.TryParse(jsonValue.ToString(), out parsedValue)))
        {
            value = parsedValue;
            return true;
        }

        errorMessage = $"'{propertyName}' must be an integer.";
        return false;
    }

    public static HashSet<string> ReadStringSet(JsonObject? arguments, string propertyName)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (arguments?[propertyName] is not JsonArray array)
        {
            return values;
        }

        foreach (var item in array)
        {
            if (item is null)
            {
                continue;
            }

            var value = item.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value.Trim());
            }
        }

        return values;
    }

    public static HashSet<byte> ReadByteSet(JsonObject? arguments, string propertyName)
    {
        var values = new HashSet<byte>();
        if (arguments?[propertyName] is not JsonArray array)
        {
            return values;
        }

        foreach (var item in array)
        {
            if (item is null)
            {
                continue;
            }

            if (item is JsonValue numericValue
                && (numericValue.TryGetValue<byte>(out var byteValue) || byte.TryParse(numericValue.ToString(), out byteValue)))
            {
                values.Add(byteValue);
            }
        }

        return values;
    }
}
