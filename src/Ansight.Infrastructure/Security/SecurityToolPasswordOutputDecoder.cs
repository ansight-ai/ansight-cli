namespace Ansight.Infrastructure.Security;

internal static class SecurityToolPasswordOutputDecoder
{
    public static string Decode(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return output;
        }

        var trimmed = output.TrimEnd('\r', '\n');
        if (LooksLikeStructuredText(trimmed) || !IsHexEncoded(trimmed))
        {
            return trimmed;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromHexString(trimmed));
            return LooksLikeStructuredText(decoded) ? decoded : trimmed;
        }
        catch (FormatException)
        {
            return trimmed;
        }
        catch (DecoderFallbackException)
        {
            return trimmed;
        }
    }

    private static bool LooksLikeStructuredText(string value)
    {
        var trimmed = value.TrimStart();
        return trimmed.Length > 0 && trimmed[0] is '{' or '[' or '"';
    }

    private static bool IsHexEncoded(string value)
    {
        if (value.Length == 0 || value.Length % 2 != 0)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
