using Ansight.Infrastructure.Logging;
using Ansight.Infrastructure.Preferences;

namespace Ansight.Infrastructure.Localisation;

public sealed class LocalisationService : ILocalisationService
{
    private readonly Lock gate = new();
    private readonly IUserPreferences userPreferences;
    private readonly ILogger log;

    private readonly Dictionary<string, LanguagePack> languagePacks;
    private LanguagePack currentLanguagePack;

    public LocalisationService(IUserPreferences userPreferences)
    {
        this.userPreferences = userPreferences ?? throw new ArgumentNullException(nameof(userPreferences));
        log = Logger.Create();

        languagePacks = new Dictionary<string, LanguagePack>(StringComparer.OrdinalIgnoreCase);

        var english = BuildDefaultEnglishPack();
        RegisterLanguagePack(english);
        currentLanguagePack = english;

        ApplyConfiguredLanguage();
    }

    public string DeviceLanguageCode => LanguageCodeHelper.DeviceLanguageCode();

    public string CurrentLanguageCode => currentLanguagePack.CultureCode;

    public string CurrentLanguageName => string.IsNullOrWhiteSpace(currentLanguagePack.NameEnglish)
        ? currentLanguagePack.Name
        : currentLanguagePack.NameEnglish;

    public string CurrentLanguageIcon => currentLanguagePack.Icon;

    public bool CurrentLanguageMachineTranslated => currentLanguagePack.MachineTranslated;

    public event EventHandler<LanguagePackEventArgs>? LanguageChanged;

    public event EventHandler<LanguagePackEventArgs>? LanguagePackApplying;

    public event EventHandler<LanguagePackEventArgs>? LanguagePackApplied;

    public string Localise(string key, params LocalisationParameter[] parameters)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        var value = FindValue(key) ?? key;

        foreach (var parameter in parameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.Key))
            {
                continue;
            }

            var parameterKey = parameter.Key.Trim();
            if (!parameterKey.StartsWith('$'))
            {
                parameterKey = "$" + parameterKey;
            }

            if (!parameterKey.EndsWith('$'))
            {
                parameterKey += "$";
            }

            value = value.Replace(parameterKey, parameter.Value ?? string.Empty, StringComparison.Ordinal);
        }

        return value;
    }

    public Task<SupportedLanguages> GetSupportedLanguages()
    {
        lock (gate)
        {
            return Task.FromResult(new SupportedLanguages
            {
                Languages = languagePacks.Values.OrderBy(value => value.NameEnglish).ToList(),
            });
        }
    }

    public Task<bool> SetLanguage(string languageCode, bool useDeviceLanguage = false)
    {
        var normalized = LanguageCodeHelper.NormalizeLanguageCode(languageCode);
        userPreferences.UseDeviceLanguage = useDeviceLanguage;
        userPreferences.PreferredLanguage = normalized;

        var selected = useDeviceLanguage
            ? LanguageCodeHelper.DeviceLanguageCode()
            : normalized;

        if (!TrySetCurrentLanguage(selected))
        {
            return Task.FromResult(false);
        }

        LanguageChanged?.Invoke(this, new LanguagePackEventArgs(CurrentLanguageCode));
        return Task.FromResult(true);
    }

    public void RegisterLanguagePack(LanguagePack languagePack)
    {
        ArgumentNullException.ThrowIfNull(languagePack);

        var code = LanguageCodeHelper.NormalizeLanguageCode(languagePack.CultureCode);
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        lock (gate)
        {
            languagePack.CultureCode = code;
            languagePacks[code] = languagePack;
        }
    }

    private void ApplyConfiguredLanguage()
    {
        var configuredCode = userPreferences.UseDeviceLanguage
            ? DeviceLanguageCode
            : userPreferences.PreferredLanguage;

        if (!TrySetCurrentLanguage(configuredCode))
        {
            TrySetCurrentLanguage("en");
        }
    }

    private bool TrySetCurrentLanguage(string languageCode)
    {
        var normalized = LanguageCodeHelper.NormalizeLanguageCode(languageCode);

        LanguagePack? next;
        lock (gate)
        {
            if (!languagePacks.TryGetValue(normalized, out next))
            {
                return false;
            }

            if (string.Equals(currentLanguagePack.CultureCode, next.CultureCode, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        LanguagePackApplying?.Invoke(this, new LanguagePackEventArgs(next.CultureCode));

        lock (gate)
        {
            currentLanguagePack = next;
        }

        log.Info($"Applied language pack '{next.CultureCode}'.");
        LanguagePackApplied?.Invoke(this, new LanguagePackEventArgs(next.CultureCode));

        return true;
    }

    private string? FindValue(string key)
    {
        var indexed = currentLanguagePack.GetIndexedValues();
        if (indexed.TryGetValue(key, out var value))
        {
            return value.Value;
        }

        if (languagePacks.TryGetValue("en", out var englishPack))
        {
            var englishValues = englishPack.GetIndexedValues();
            if (englishValues.TryGetValue(key, out var englishValue))
            {
                return englishValue.Value;
            }
        }

        return null;
    }

    private static LanguagePack BuildDefaultEnglishPack()
    {
        return new LanguagePack
        {
            Name = "English",
            NameEnglish = "English",
            Icon = "🇬🇧",
            CultureCode = "en",
            IsDefaultLanguagePack = true,
            Values =
            [
                new LanguageValue { Key = "Global_Ok", Value = "OK" },
                new LanguageValue { Key = "Global_Cancel", Value = "Cancel" },
                new LanguageValue { Key = "App_Title", Value = "Ansight" },
                new LanguageValue { Key = "Licensing_Expired", Value = "Your licence has expired." },
            ],
        };
    }
}
