using System.Globalization;

namespace Ansight.Host.Runtime.Archives;

internal static class SessionArchiveJsonValueReader
{
    private static readonly JsonSerializerOptions ArchiveJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    internal static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    internal static string? FirstString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = TryGetString(element, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    internal static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => null
        };
    }

    internal static DateTimeOffset? FirstTimestamp(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetProperty(element, propertyName, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(
                    value.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                return parsed;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var unixMilliseconds))
            {
                try
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    internal static int? FirstInt(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetProperty(element, propertyName, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numericValue))
            {
                return numericValue;
            }

            if (value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stringValue))
            {
                return stringValue;
            }
        }

        return null;
    }

    internal static long? FirstInt64(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetProperty(element, propertyName, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var numericValue))
            {
                return numericValue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var doubleValue))
            {
                return (long)Math.Round(doubleValue, MidpointRounding.AwayFromZero);
            }

            if (value.ValueKind == JsonValueKind.String
                && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stringValue))
            {
                return stringValue;
            }
        }

        return null;
    }

    internal static double? FirstDouble(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetProperty(element, propertyName, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var numericValue))
            {
                return numericValue;
            }

            if (value.ValueKind == JsonValueKind.String
                && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var stringValue))
            {
                return stringValue;
            }
        }

        return null;
    }

    internal static byte? FirstByte(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetProperty(element, propertyName, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetByte(out var numericValue))
            {
                return numericValue;
            }

            if (value.ValueKind == JsonValueKind.String
                && byte.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stringValue))
            {
                return stringValue;
            }
        }

        return null;
    }

    internal static LogPriority? TryGetLogPriority(JsonElement element)
    {
        var value = FirstString(element, "priority", "level", "severity");
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Enum.TryParse<LogPriority>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return value.Trim().ToUpperInvariant() switch
        {
            "V" or "TRACE" => LogPriority.Verbose,
            "D" => LogPriority.Debug,
            "I" or "INFO" => LogPriority.Information,
            "W" or "WARN" => LogPriority.Warning,
            "E" => LogPriority.Error,
            "F" => LogPriority.Fatal,
            _ => null
        };
    }

    internal static string ResolveOfflineEventKind(int eventKind)
    {
        return eventKind switch
        {
            1 => "DEBUG",
            2 => "INFO",
            3 => "WARNING",
            4 => "ERROR",
            5 => "EXCEPTION",
            6 => "GC",
            7 => "NAVIGATION",
            8 => "SCREENVIEWED",
            9 => "LIFECYCLE",
            _ => "EVENT"
        };
    }

    internal static bool TryDeserialize<T>(JsonElement element, out T? value)
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(element.GetRawText(), ArchiveJsonOptions);
            return value is not null;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }

    internal static bool IsWireEventType(string? type)
    {
        return string.Equals(type, WebSocketClientEventConstants.ClientLog, StringComparison.Ordinal)
               || string.Equals(type, WebSocketClientEventConstants.ClientMetricChannels, StringComparison.Ordinal)
               || string.Equals(type, WebSocketClientEventConstants.ClientMetrics, StringComparison.Ordinal)
               || string.Equals(type, WebSocketClientEventConstants.ClientEvents, StringComparison.Ordinal)
               || string.Equals(type, WebSocketClientEventConstants.ClientTouchInput, StringComparison.Ordinal)
               || string.Equals(type, WebSocketClientEventConstants.ClientAppState, StringComparison.Ordinal)
               || string.Equals(type, WebSocketClientEventConstants.DeviceAppProfile, StringComparison.Ordinal)
               || string.Equals(type, WebSocketClientEventConstants.ClientJpeg, StringComparison.Ordinal);
    }

    internal static string? NormalizeOptionalString(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    internal static string NormalizeColorHex(string? colorHex)
    {
        if (string.IsNullOrWhiteSpace(colorHex))
        {
            return "#4A90E2";
        }

        var trimmed = colorHex.Trim();
        if (trimmed.Length == 7 && trimmed[0] == '#')
        {
            return trimmed.ToUpperInvariant();
        }

        return "#4A90E2";
    }

    internal static string NormalizeCoordinateSpace(string? value)
    {
        return string.Equals(value?.Trim(), "w", StringComparison.OrdinalIgnoreCase)
            ? "window"
            : string.IsNullOrWhiteSpace(value)
                ? "window"
                : value.Trim();
    }

    internal static string NormalizeCoordinateUnit(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "px" or "pixel" or "pixels" => "pixels",
            "pt" or "point" or "points" => "points",
            "n" or "unit" or "ratio" or "normalized" => "normalized",
            _ => string.IsNullOrWhiteSpace(value) ? "pixels" : value.Trim()
        };
    }

    internal static string NormalizeTouchAction(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "0" or "began" or "begin" or "start" or "down" => "down",
            "1" or "move" or "moved" => "move",
            "2" or "ended" or "end" or "up" => "up",
            "3" or "cancelled" or "canceled" or "cancel" => "cancel",
            "5" or "hoverenter" => "hoverEnter",
            "6" or "hovermove" => "hoverMove",
            "7" or "hoverexit" => "hoverExit",
            var normalized when !string.IsNullOrWhiteSpace(normalized) => normalized,
            _ => "unknown"
        };
    }

    internal static string NormalizeImageFormat(string value)
    {
        var normalized = value.Trim().TrimStart('.').ToLowerInvariant();
        if (normalized.Contains("jpeg", StringComparison.Ordinal) || normalized == "jpg")
        {
            return "jpeg";
        }

        if (normalized.Contains("png", StringComparison.Ordinal))
        {
            return "png";
        }

        return string.IsNullOrWhiteSpace(normalized) ? "jpeg" : normalized;
    }

    internal static string BuildFrameId(DateTimeOffset capturedAtUtc, int index)
    {
        return $"frame-{capturedAtUtc.UtcDateTime:yyyyMMddHHmmssfff}-{index + 1:D4}";
    }

    internal static string BuildTouchId(DateTimeOffset capturedAtUtc, long pointerId)
    {
        return $"touch-{capturedAtUtc.UtcDateTime:yyyyMMddHHmmssfff}-{pointerId}";
    }

}
