namespace Ansight.Host.Models.Pairing;

public sealed class PairingConfigDuration
{
    private PairingConfigDuration(int months, TimeSpan offset, string display)
    {
        Months = months;
        Offset = offset;
        Display = display;
    }

    public int Months { get; }

    public TimeSpan Offset { get; }

    public string Display { get; }

    public static PairingConfigDuration Default { get; } = new(1, TimeSpan.Zero, "1 month");

    public DateTimeOffset Apply(DateTimeOffset fromUtc)
    {
        return fromUtc.AddMonths(Months).Add(Offset);
    }

    public static bool TryParse(string? input, out PairingConfigDuration duration, out string? error)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            duration = Default;
            error = null;
            return true;
        }

        var text = input.Trim().ToLowerInvariant();

        var numberText = new StringBuilder();
        var index = 0;
        while (index < text.Length && char.IsDigit(text[index]))
        {
            numberText.Append(text[index]);
            index++;
        }

        if (numberText.Length == 0 || !int.TryParse(numberText.ToString(), out var value) || value <= 0)
        {
            duration = Default;
            error = "Duration must start with a positive number.";
            return false;
        }

        var unit = text[index..].Trim();
        if (string.IsNullOrWhiteSpace(unit))
        {
            duration = Default;
            error = "Missing duration unit. Try '1mo', '7d', '12h', or '30m'.";
            return false;
        }

        switch (unit)
        {
            case "mo":
            case "month":
            case "months":
                duration = new PairingConfigDuration(value, TimeSpan.Zero, value == 1 ? "1 month" : $"{value} months");
                error = null;
                return true;

            case "w":
            case "week":
            case "weeks":
                duration = new PairingConfigDuration(0, TimeSpan.FromDays(value * 7d), value == 1 ? "1 week" : $"{value} weeks");
                error = null;
                return true;

            case "d":
            case "day":
            case "days":
                duration = new PairingConfigDuration(0, TimeSpan.FromDays(value), value == 1 ? "1 day" : $"{value} days");
                error = null;
                return true;

            case "h":
            case "hr":
            case "hour":
            case "hours":
                duration = new PairingConfigDuration(0, TimeSpan.FromHours(value), value == 1 ? "1 hour" : $"{value} hours");
                error = null;
                return true;

            case "m":
            case "min":
            case "mins":
            case "minute":
            case "minutes":
                duration = new PairingConfigDuration(0, TimeSpan.FromMinutes(value), value == 1 ? "1 minute" : $"{value} minutes");
                error = null;
                return true;

            default:
                duration = Default;
                error = $"Unsupported duration unit '{unit}'. Use mo, w, d, h, or m.";
                return false;
        }
    }
}
