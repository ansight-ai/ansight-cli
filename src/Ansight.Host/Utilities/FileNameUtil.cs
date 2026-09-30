namespace Ansight.Host.Utilities;

public static class FileNameUtil
{
    public static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "ansight-client";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return builder.ToString().Replace(' ', '_').ToLowerInvariant();
    }
}
