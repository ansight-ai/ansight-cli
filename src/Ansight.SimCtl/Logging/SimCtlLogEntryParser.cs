using System.Text;
using System.Text.Json;

namespace Ansight.SimCtl;

public static class SimCtlLogEntryParser
{
    public static bool TryParse(string? line, out SimCtlLogEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        return TryParse(Encoding.UTF8.GetBytes(line), out entry);
    }

    public static bool TryParse(ReadOnlySpan<byte> utf8Line, out SimCtlLogEntry? entry)
    {
        entry = null;
        if (utf8Line.IsEmpty)
        {
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(utf8Line, isFinalBlock: true, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            var timestampUtc = default(DateTimeOffset);
            var hasTimestamp = false;
            var processId = 0;
            var hasProcessId = false;
            var threadId = 0L;
            var priority = SimCtlLogPriority.Unknown;
            var subsystem = string.Empty;
            var category = string.Empty;
            var processImagePath = string.Empty;
            var message = string.Empty;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                var property = ResolveProperty(ref reader);
                if (!reader.Read())
                {
                    return false;
                }

                switch (property)
                {
                    case SimCtlLogProperty.Unknown:
                        reader.Skip();
                        break;
                    case SimCtlLogProperty.Timestamp:
                        hasTimestamp = reader.TokenType == JsonTokenType.String
                                       && !reader.ValueIsEscaped
                                       && TryParseTimestamp(reader.ValueSpan, out timestampUtc);
                        break;
                    case SimCtlLogProperty.ProcessId:
                        hasProcessId = reader.TokenType == JsonTokenType.Number
                                       && reader.TryGetInt32(out processId);
                        break;
                    case SimCtlLogProperty.ThreadId:
                        if (reader.TokenType == JsonTokenType.Number)
                        {
                            reader.TryGetInt64(out threadId);
                        }
                        break;
                    case SimCtlLogProperty.MessageType:
                        priority = MapPriority(ref reader);
                        break;
                    case SimCtlLogProperty.Subsystem:
                        subsystem = GetString(ref reader);
                        break;
                    case SimCtlLogProperty.Category:
                        category = GetString(ref reader);
                        break;
                    case SimCtlLogProperty.ProcessImagePath:
                        processImagePath = GetString(ref reader);
                        break;
                    case SimCtlLogProperty.EventMessage:
                        message = GetString(ref reader);
                        break;
                }
            }

            if (!hasTimestamp || !hasProcessId)
            {
                return false;
            }

            entry = new SimCtlLogEntry(
                timestampUtc,
                processId,
                threadId,
                priority,
                subsystem,
                category,
                processImagePath,
                message);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static SimCtlLogProperty ResolveProperty(ref Utf8JsonReader reader)
    {
        if (reader.ValueTextEquals("timestamp"u8))
        {
            return SimCtlLogProperty.Timestamp;
        }

        if (reader.ValueTextEquals("processID"u8))
        {
            return SimCtlLogProperty.ProcessId;
        }

        if (reader.ValueTextEquals("threadID"u8))
        {
            return SimCtlLogProperty.ThreadId;
        }

        if (reader.ValueTextEquals("messageType"u8))
        {
            return SimCtlLogProperty.MessageType;
        }

        if (reader.ValueTextEquals("subsystem"u8))
        {
            return SimCtlLogProperty.Subsystem;
        }

        if (reader.ValueTextEquals("category"u8))
        {
            return SimCtlLogProperty.Category;
        }

        if (reader.ValueTextEquals("processImagePath"u8))
        {
            return SimCtlLogProperty.ProcessImagePath;
        }

        return reader.ValueTextEquals("eventMessage"u8)
            ? SimCtlLogProperty.EventMessage
            : SimCtlLogProperty.Unknown;
    }

    private static string GetString(ref Utf8JsonReader reader)
        => reader.TokenType == JsonTokenType.String ? reader.GetString() ?? string.Empty : string.Empty;

    private static SimCtlLogPriority MapPriority(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            return SimCtlLogPriority.Unknown;
        }

        if (EqualsIgnoreAsciiCase(reader.ValueSpan, "debug"u8))
        {
            return SimCtlLogPriority.Debug;
        }

        if (EqualsIgnoreAsciiCase(reader.ValueSpan, "info"u8))
        {
            return SimCtlLogPriority.Information;
        }

        if (EqualsIgnoreAsciiCase(reader.ValueSpan, "default"u8))
        {
            return SimCtlLogPriority.Default;
        }

        if (EqualsIgnoreAsciiCase(reader.ValueSpan, "error"u8))
        {
            return SimCtlLogPriority.Error;
        }

        return EqualsIgnoreAsciiCase(reader.ValueSpan, "fault"u8)
            ? SimCtlLogPriority.Fault
            : SimCtlLogPriority.Unknown;
    }

    private static bool TryParseTimestamp(ReadOnlySpan<byte> value, out DateTimeOffset timestampUtc)
    {
        timestampUtc = default;
        if (value.Length < 24
            || value[4] != '-'
            || value[7] != '-'
            || value[10] != ' '
            || value[13] != ':'
            || value[16] != ':'
            || !TryParseDigits(value, 0, 4, out var year)
            || !TryParseDigits(value, 5, 2, out var month)
            || !TryParseDigits(value, 8, 2, out var day)
            || !TryParseDigits(value, 11, 2, out var hour)
            || !TryParseDigits(value, 14, 2, out var minute)
            || !TryParseDigits(value, 17, 2, out var second))
        {
            return false;
        }

        var offsetIndex = 19;
        var fractionTicks = 0L;
        if (value[offsetIndex] == '.')
        {
            offsetIndex++;
            var fractionLength = 0;
            while (offsetIndex + fractionLength < value.Length
                   && value[offsetIndex + fractionLength] is >= (byte)'0' and <= (byte)'9')
            {
                if (fractionLength < 7)
                {
                    fractionTicks = (fractionTicks * 10) + value[offsetIndex + fractionLength] - '0';
                }

                fractionLength++;
            }

            if (fractionLength == 0)
            {
                return false;
            }

            for (var index = Math.Min(fractionLength, 7); index < 7; index++)
            {
                fractionTicks *= 10;
            }

            offsetIndex += fractionLength;
        }

        if (offsetIndex >= value.Length || value[offsetIndex] is not ((byte)'+' or (byte)'-'))
        {
            return false;
        }

        var offsetSign = value[offsetIndex] == '-' ? -1 : 1;
        var offsetValue = value[(offsetIndex + 1)..];
        int offsetHour;
        int offsetMinute;
        if (offsetValue.Length == 4)
        {
            if (!TryParseDigits(offsetValue, 0, 2, out offsetHour)
                || !TryParseDigits(offsetValue, 2, 2, out offsetMinute))
            {
                return false;
            }
        }
        else if (offsetValue.Length == 5 && offsetValue[2] == ':')
        {
            if (!TryParseDigits(offsetValue, 0, 2, out offsetHour)
                || !TryParseDigits(offsetValue, 3, 2, out offsetMinute))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        if (!IsValidDateTime(year, month, day, hour, minute, second)
            || offsetHour > 14
            || offsetMinute > 59
            || (offsetHour == 14 && offsetMinute != 0))
        {
            return false;
        }

        var offset = new TimeSpan(offsetSign * offsetHour, offsetSign * offsetMinute, 0);
        timestampUtc = new DateTimeOffset(year, month, day, hour, minute, second, offset)
            .AddTicks(fractionTicks)
            .ToUniversalTime();
        return true;
    }

    private static bool TryParseDigits(
        ReadOnlySpan<byte> value,
        int start,
        int length,
        out int result)
    {
        result = 0;
        if (start < 0 || length <= 0 || start + length > value.Length)
        {
            return false;
        }

        for (var index = start; index < start + length; index++)
        {
            var digit = value[index] - '0';
            if ((uint)digit > 9)
            {
                result = 0;
                return false;
            }

            result = (result * 10) + digit;
        }

        return true;
    }

    private static bool EqualsIgnoreAsciiCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> expectedLowercase)
    {
        if (value.Length != expectedLowercase.Length)
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character is >= (byte)'A' and <= (byte)'Z')
            {
                character += (byte)('a' - 'A');
            }

            if (character != expectedLowercase[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidDateTime(
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second)
        => year is >= 1 and <= 9999
           && month is >= 1 and <= 12
           && day >= 1
           && day <= DateTime.DaysInMonth(year, month)
           && hour is >= 0 and <= 23
           && minute is >= 0 and <= 59
           && second is >= 0 and <= 59;

    private enum SimCtlLogProperty
    {
        Unknown,
        Timestamp,
        ProcessId,
        ThreadId,
        MessageType,
        Subsystem,
        Category,
        ProcessImagePath,
        EventMessage
    }
}
