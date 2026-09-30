using System.Globalization;

namespace Ansight.Adb;

public static class AdbLogEntryParser
{
    public static bool TryParse(string? line, out AdbLogEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var remaining = line.AsSpan();
        if (!TryReadToken(ref remaining, out var date)
            || !TryReadToken(ref remaining, out var time)
            || !TryReadToken(ref remaining, out var processIdOrOffsetValue))
        {
            return false;
        }

        var offset = TimeSpan.Zero;
        int processId;
        if (TryParseOffset(processIdOrOffsetValue, out var parsedOffset))
        {
            offset = parsedOffset;
            if (!TryReadToken(ref remaining, out var processIdValue)
                || !int.TryParse(processIdValue, NumberStyles.None, CultureInfo.InvariantCulture, out processId))
            {
                return false;
            }
        }
        else if (!int.TryParse(
                     processIdOrOffsetValue,
                     NumberStyles.None,
                     CultureInfo.InvariantCulture,
                     out processId))
        {
            return false;
        }

        if (!TryReadToken(ref remaining, out var threadIdValue)
            || !TryReadToken(ref remaining, out var priorityValue)
            || !TryParseTimestamp(date, time, offset, out var timestampUtc)
            || !int.TryParse(threadIdValue, NumberStyles.None, CultureInfo.InvariantCulture, out var threadId)
            || priorityValue.Length != 1)
        {
            return false;
        }

        remaining = TrimStart(remaining);
        var separatorIndex = remaining.IndexOf(':');
        if (separatorIndex < 0)
        {
            return false;
        }

        var tag = remaining[..separatorIndex].Trim();
        var message = remaining[(separatorIndex + 1)..];
        if (!message.IsEmpty && message[0] == ' ')
        {
            message = message[1..];
        }

        entry = new AdbLogEntry(
            timestampUtc,
            processId,
            threadId,
            MapPriority(priorityValue[0]),
            tag.ToString(),
            message.ToString());
        return true;
    }

    internal static bool IsIgnorableLine(string? line)
        => string.IsNullOrWhiteSpace(line)
           || line.TrimStart().StartsWith("---------", StringComparison.Ordinal);

    private static bool TryParseTimestamp(
        ReadOnlySpan<char> date,
        ReadOnlySpan<char> time,
        TimeSpan offset,
        out DateTimeOffset timestampUtc)
    {
        timestampUtc = default;
        if (date.Length != 10
            || time.Length < 8
            || date[4] != '-'
            || date[7] != '-'
            || time[2] != ':'
            || time[5] != ':'
            || !TryParseDigits(date, 0, 4, out var year)
            || !TryParseDigits(date, 5, 2, out var month)
            || !TryParseDigits(date, 8, 2, out var day)
            || !TryParseDigits(time, 0, 2, out var hour)
            || !TryParseDigits(time, 3, 2, out var minute)
            || !TryParseDigits(time, 6, 2, out var second))
        {
            return false;
        }

        var fractionTicks = 0L;
        if (time.Length > 8)
        {
            var fraction = time[8..];
            if (fraction.Length is < 2 or > 8 || fraction[0] != '.')
            {
                return false;
            }

            fraction = fraction[1..];
            for (var index = 0; index < fraction.Length; index++)
            {
                var digit = fraction[index] - '0';
                if ((uint)digit > 9)
                {
                    return false;
                }

                fractionTicks = (fractionTicks * 10) + digit;
            }

            for (var index = fraction.Length; index < 7; index++)
            {
                fractionTicks *= 10;
            }
        }

        if (!IsValidDateTime(year, month, day, hour, minute, second))
        {
            return false;
        }

        timestampUtc = new DateTimeOffset(year, month, day, hour, minute, second, offset)
            .AddTicks(fractionTicks)
            .ToUniversalTime();
        return true;
    }

    private static bool TryParseOffset(ReadOnlySpan<char> value, out TimeSpan offset)
    {
        offset = default;
        if (value.Length is not (5 or 6)
            || value[0] is not ('+' or '-'))
        {
            return false;
        }

        var hasSeparator = value.Length == 6;
        if (hasSeparator && value[3] != ':')
        {
            return false;
        }

        if (!TryParseDigits(value, 1, 2, out var hours)
            || !TryParseDigits(value, hasSeparator ? 4 : 3, 2, out var minutes)
            || hours > 14
            || minutes > 59
            || (hours == 14 && minutes != 0))
        {
            return false;
        }

        offset = new TimeSpan(hours, minutes, 0);
        if (value[0] == '-')
        {
            offset = -offset;
        }

        return true;
    }

    private static bool TryReadToken(
        ref ReadOnlySpan<char> remaining,
        out ReadOnlySpan<char> token)
    {
        remaining = TrimStart(remaining);
        var endIndex = remaining.IndexOfAny(' ', '\t');
        if (endIndex < 0)
        {
            token = remaining;
            remaining = [];
            return !token.IsEmpty;
        }

        token = remaining[..endIndex];
        remaining = remaining[endIndex..];
        return !token.IsEmpty;
    }

    private static ReadOnlySpan<char> TrimStart(ReadOnlySpan<char> value)
    {
        var index = 0;
        while (index < value.Length && char.IsWhiteSpace(value[index]))
        {
            index++;
        }

        return value[index..];
    }

    private static bool TryParseDigits(
        ReadOnlySpan<char> value,
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

    private static AdbLogPriority MapPriority(char value)
    {
        return value switch
        {
            'V' => AdbLogPriority.Verbose,
            'D' => AdbLogPriority.Debug,
            'I' => AdbLogPriority.Information,
            'W' => AdbLogPriority.Warning,
            'E' => AdbLogPriority.Error,
            'F' or 'A' => AdbLogPriority.Fatal,
            _ => AdbLogPriority.Unknown
        };
    }
}
