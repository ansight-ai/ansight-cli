using System.Globalization;

namespace Ansight.Infrastructure.Localisation;

public static class LanguageCodeHelper
{
    public static string DeviceLanguageCode()
    {
        return NormalizeLanguageCode(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
    }

    public static string NormalizeLanguageCode(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return "en";
        }

        return languageCode.Trim().ToLowerInvariant();
    }
}
