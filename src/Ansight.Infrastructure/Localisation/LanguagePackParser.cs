using System.Text.Json;

namespace Ansight.Infrastructure.Localisation;

public static class LanguagePackParser
{
    public static LanguagePack Parse(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json, isFinalBlock: true, state: default);

        var pack = new LanguagePack();
        var values = new List<LanguageValue>();

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected start of object.");
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected property name.");
            }

            var propName = reader.GetString();
            reader.Read();

            switch (propName)
            {
                case nameof(LanguagePack.Name):
                    pack.Name = reader.GetString() ?? string.Empty;
                    break;
                case nameof(LanguagePack.NameEnglish):
                    pack.NameEnglish = reader.GetString() ?? string.Empty;
                    break;
                case nameof(LanguagePack.CultureCode):
                    pack.CultureCode = reader.GetString() ?? "en";
                    break;
                case nameof(LanguagePack.Icon):
                    pack.Icon = reader.GetString() ?? string.Empty;
                    break;
                case nameof(LanguagePack.IsDefaultLanguagePack):
                    pack.IsDefaultLanguagePack = reader.GetBoolean();
                    break;
                case nameof(LanguagePack.IsPublic):
                    pack.IsPublic = reader.GetBoolean();
                    break;
                case nameof(LanguagePack.MachineTranslated):
                    pack.MachineTranslated = reader.GetBoolean();
                    break;
                case nameof(LanguagePack.Values):
                    if (reader.TokenType != JsonTokenType.StartArray && !reader.Read())
                    {
                        throw new JsonException("Expected start of array.");
                    }

                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        values.Add(ReadLanguageValue(ref reader));
                    }
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        pack.Values = values;
        return pack;
    }

    private static LanguageValue ReadLanguageValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject && !reader.Read())
        {
            throw new JsonException("Expected start of object for LanguageValue.");
        }

        var value = new LanguageValue();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected property name in LanguageValue.");
            }

            var propName = reader.GetString();
            reader.Read();

            switch (propName)
            {
                case nameof(LanguageValue.Key):
                    value.Key = reader.GetString() ?? string.Empty;
                    break;
                case nameof(LanguageValue.Value):
                    value.Value = reader.GetString() ?? string.Empty;
                    break;
                case nameof(LanguageValue.Description):
                    value.Description = reader.GetString() ?? string.Empty;
                    break;
                case nameof(LanguageValue.Parameterised):
                    value.Parameterised = reader.GetBoolean();
                    break;
                case nameof(LanguageValue.IsAppResource):
                    value.IsAppResource = reader.GetBoolean();
                    break;
                case nameof(LanguageValue.MachineTranslated):
                    value.MachineTranslated = reader.GetBoolean();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return value;
    }
}
