namespace Ansight.Infrastructure.Localisation;

public sealed class LanguagePackEventArgs : EventArgs
{
    public LanguagePackEventArgs(string languageCode)
    {
        LanguageCode = languageCode;
    }

    public string LanguageCode { get; }
}
