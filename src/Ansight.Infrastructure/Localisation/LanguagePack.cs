namespace Ansight.Infrastructure.Localisation;

public sealed class LanguagePack
{
    public string Name { get; set; } = string.Empty;

    public string NameEnglish { get; set; } = string.Empty;

    public string CultureCode { get; set; } = "en";

    public string Icon { get; set; } = "🇬🇧";

    public bool IsDefaultLanguagePack { get; set; }

    public bool IsPublic { get; set; } = true;

    public bool MachineTranslated { get; set; }

    public List<LanguageValue> Values { get; set; } = new();

    public IReadOnlyDictionary<string, LanguageValue> GetIndexedValues()
    {
        var dictionary = new Dictionary<string, LanguageValue>(StringComparer.Ordinal);

        foreach (var value in Values)
        {
            if (string.IsNullOrWhiteSpace(value?.Key))
            {
                continue;
            }

            dictionary[value.Key] = value;
        }

        return dictionary;
    }
}
