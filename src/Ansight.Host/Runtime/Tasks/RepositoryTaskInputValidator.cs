using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Tasks;

internal static class RepositoryTaskInputValidator
{
    public static bool TryValidateAndApplyDefaults(
        JsonObject schema,
        JsonObject? suppliedInput,
        out JsonObject input,
        out string errorMessage)
    {
        input = suppliedInput?.DeepClone().AsObject() ?? new JsonObject();
        errorMessage = string.Empty;
        var properties = schema["properties"] as JsonObject ?? new JsonObject();
        var allowsAdditionalProperties = schema["additionalProperties"]?.GetValue<bool>() ?? true;
        if (!allowsAdditionalProperties)
        {
            var unknownProperty = input.Select(static property => property.Key)
                .FirstOrDefault(name => !properties.ContainsKey(name));
            if (unknownProperty is not null)
            {
                errorMessage = $"Task input contains unknown property '{unknownProperty}'.";
                return false;
            }
        }

        foreach (var property in properties)
        {
            if (input[property.Key] is null
                && property.Value is JsonObject propertySchema
                && propertySchema["default"] is { } defaultValue)
            {
                input[property.Key] = defaultValue.DeepClone();
            }
        }

        if (schema["required"] is JsonArray required)
        {
            foreach (var requiredNameNode in required)
            {
                var requiredName = requiredNameNode?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(requiredName) && input[requiredName] is null)
                {
                    errorMessage = $"Task input property '{requiredName}' is required.";
                    return false;
                }
            }
        }

        foreach (var property in input)
        {
            if (property.Value is null || properties[property.Key] is not JsonObject propertySchema)
            {
                continue;
            }

            var expectedType = propertySchema["type"]?.GetValue<string>();
            if (expectedType is not null && !MatchesType(property.Value, expectedType))
            {
                errorMessage = $"Task input property '{property.Key}' must be {expectedType}.";
                return false;
            }

            if (propertySchema["enum"] is JsonArray allowedValues
                && !allowedValues.Any(allowed => JsonNode.DeepEquals(allowed, property.Value)))
            {
                errorMessage = $"Task input property '{property.Key}' is not one of its allowed values.";
                return false;
            }

            if (property.Value is JsonValue numericValue
                && TryReadNumber(numericValue, out var number))
            {
                if (propertySchema["minimum"] is JsonValue minimumValue
                    && TryReadNumber(minimumValue, out var minimum)
                    && number < minimum)
                {
                    errorMessage = $"Task input property '{property.Key}' must be at least {minimum}.";
                    return false;
                }

                if (propertySchema["maximum"] is JsonValue maximumValue
                    && TryReadNumber(maximumValue, out var maximum)
                    && number > maximum)
                {
                    errorMessage = $"Task input property '{property.Key}' must be at most {maximum}.";
                    return false;
                }
            }
        }

        return true;
    }

    private static bool MatchesType(JsonNode value, string expectedType)
    {
        return expectedType switch
        {
            "object" => value is JsonObject,
            "array" => value is JsonArray,
            "string" => value is JsonValue stringValue && stringValue.TryGetValue<string>(out _),
            "boolean" => value is JsonValue booleanValue && booleanValue.TryGetValue<bool>(out _),
            "integer" => value is JsonValue integerValue && IsInteger(integerValue),
            "number" => value is JsonValue numberValue && TryReadNumber(numberValue, out _),
            "null" => value.GetValueKind() == System.Text.Json.JsonValueKind.Null,
            _ => true
        };
    }

    private static bool IsInteger(JsonValue value)
        => value.TryGetValue<int>(out _)
           || value.TryGetValue<long>(out _)
           || value.TryGetValue<short>(out _)
           || value.TryGetValue<byte>(out _)
           || value.TryGetValue<uint>(out _)
           || value.TryGetValue<ulong>(out _);

    private static bool TryReadNumber(JsonValue value, out decimal number)
    {
        if (value.TryGetValue<decimal>(out number))
        {
            return true;
        }

        if (value.TryGetValue<int>(out var integer))
        {
            number = integer;
            return true;
        }

        if (value.TryGetValue<long>(out var longInteger))
        {
            number = longInteger;
            return true;
        }

        if (value.TryGetValue<uint>(out var unsignedInteger))
        {
            number = unsignedInteger;
            return true;
        }

        if (value.TryGetValue<ulong>(out var unsignedLongInteger))
        {
            number = unsignedLongInteger;
            return true;
        }

        if (value.TryGetValue<double>(out var doubleValue)
            && double.IsFinite(doubleValue)
            && doubleValue is >= (double)decimal.MinValue and <= (double)decimal.MaxValue)
        {
            number = (decimal)doubleValue;
            return true;
        }

        number = default;
        return false;
    }
}
