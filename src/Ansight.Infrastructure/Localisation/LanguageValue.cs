namespace Ansight.Infrastructure.Localisation;

public sealed class LanguageValue
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public bool Parameterised { get; set; }

    public bool IsAppResource { get; set; } = true;

    public bool MachineTranslated { get; set; }
}
