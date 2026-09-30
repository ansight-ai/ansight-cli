namespace Ansight.Infrastructure.Localisation;

public interface ILocalisationService
{
    string DeviceLanguageCode { get; }

    string CurrentLanguageCode { get; }

    string CurrentLanguageName { get; }

    string CurrentLanguageIcon { get; }

    bool CurrentLanguageMachineTranslated { get; }

    event EventHandler<LanguagePackEventArgs>? LanguageChanged;

    event EventHandler<LanguagePackEventArgs>? LanguagePackApplying;

    event EventHandler<LanguagePackEventArgs>? LanguagePackApplied;

    string Localise(string key, params LocalisationParameter[] parameters);

    Task<SupportedLanguages> GetSupportedLanguages();

    Task<bool> SetLanguage(string languageCode, bool useDeviceLanguage = false);

    void RegisterLanguagePack(LanguagePack languagePack);
}
