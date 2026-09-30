using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal static class NetworkInspectionArguments
{
    public static string? ReadString(JsonObject? arguments, string name, bool required = false)
    {
        if (arguments?[name] is null && !required)
        {
            return null;
        }

        if (arguments?[name] is not JsonValue value || !value.TryGetValue<string>(out var result)
            || string.IsNullOrWhiteSpace(result))
        {
            throw new ArgumentException($"{name} must be a non-empty string.");
        }

        return result.Trim();
    }

    public static int ReadLimit(JsonObject? arguments, string name, int defaultValue, int maximum)
    {
        if (arguments?[name] is null)
        {
            return defaultValue;
        }

        if (arguments[name] is not JsonValue value || !value.TryGetValue<int>(out var result)
            || result < 1 || result > maximum)
        {
            throw new ArgumentException($"{name} must be an integer between 1 and {maximum}.");
        }

        return result;
    }

    public static bool ReadBoolean(JsonObject? arguments, string name)
    {
        if (arguments?[name] is null)
        {
            return false;
        }

        if (arguments[name] is not JsonValue value || !value.TryGetValue<bool>(out var result))
        {
            throw new ArgumentException($"{name} must be a boolean.");
        }

        return result;
    }

    public static DateTimeOffset? ReadTimestamp(JsonObject? arguments, string name)
    {
        var text = ReadString(arguments, name);
        if (text is null)
        {
            return null;
        }

        if (!Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
        {
            throw new ArgumentException($"{name} must be an ISO-8601 timestamp with a timezone.");
        }

        return value.ToUniversalTime();
    }

    public static string[] ReadValues(JsonObject? arguments, string name, bool statuses = false)
    {
        if (arguments?[name] is null)
        {
            return [];
        }

        if (arguments[name] is not JsonArray values)
        {
            throw new ArgumentException($"{name} must be an array.");
        }

        var normalized = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var node in values)
        {
            if (statuses && node is JsonValue number && number.TryGetValue<int>(out var status)
                && status is >= 100 and <= 599)
            {
                normalized.Add(status.ToString(CultureInfo.InvariantCulture));
            }
            else if (node is JsonValue value && value.TryGetValue<string>(out var text)
                     && !string.IsNullOrWhiteSpace(text))
            {
                var trimmed = text.Trim();
                if (statuses && trimmed is not ("1xx" or "2xx" or "3xx" or "4xx" or "5xx"))
                {
                    throw new ArgumentException("statuses must contain integer codes from 100 to 599 or classes 1xx through 5xx.");
                }

                normalized.Add(statuses ? trimmed : trimmed.ToUpperInvariant());
            }
            else
            {
                throw new ArgumentException(statuses
                    ? "statuses must contain integer codes from 100 to 599 or classes 1xx through 5xx."
                    : $"{name} must contain non-empty strings.");
            }
        }

        return normalized.ToArray();
    }
}
