namespace Ansight.Infrastructure.Theming;

public readonly struct Color : IEquatable<Color>
{
    public Color(byte red, byte green, byte blue, byte alpha = byte.MaxValue)
    {
        Red = red;
        Green = green;
        Blue = blue;
        Alpha = alpha;
    }

    public byte Red { get; }

    public byte Green { get; }

    public byte Blue { get; }

    public byte Alpha { get; }

    public static Color FromHex(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(value));
        }

        var text = value.Trim().TrimStart('#');

        return text.Length switch
        {
            3 => new Color(
                Convert.ToByte(new string(text[0], 2), 16),
                Convert.ToByte(new string(text[1], 2), 16),
                Convert.ToByte(new string(text[2], 2), 16)),
            6 => new Color(
                Convert.ToByte(text[0..2], 16),
                Convert.ToByte(text[2..4], 16),
                Convert.ToByte(text[4..6], 16)),
            8 => new Color(
                Convert.ToByte(text[2..4], 16),
                Convert.ToByte(text[4..6], 16),
                Convert.ToByte(text[6..8], 16),
                Convert.ToByte(text[0..2], 16)),
            _ => throw new FormatException($"Unsupported color hex format '{value}'."),
        };
    }

    public Color WithAlpha(byte alpha)
    {
        return new Color(Red, Green, Blue, alpha);
    }

    public string ToHex(bool includeAlpha = true)
    {
        return includeAlpha
            ? $"#{Alpha:X2}{Red:X2}{Green:X2}{Blue:X2}"
            : $"#{Red:X2}{Green:X2}{Blue:X2}";
    }

    public bool Equals(Color other)
    {
        return Red == other.Red
               && Green == other.Green
               && Blue == other.Blue
               && Alpha == other.Alpha;
    }

    public override bool Equals(object? obj)
    {
        return obj is Color other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Red, Green, Blue, Alpha);
    }

    public static bool operator ==(Color left, Color right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(Color left, Color right)
    {
        return !(left == right);
    }
}
