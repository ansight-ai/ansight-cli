namespace Ansight.Infrastructure.Localisation;

public readonly record struct LocalisationParameter(string Key, string Value)
{
    public static implicit operator LocalisationParameter((string key, string value) parameter)
        => new(parameter.key, parameter.value);
}
