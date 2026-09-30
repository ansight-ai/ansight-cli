using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Automation;

internal sealed class RepositoryAutomationTriggerCondition
{
    private static readonly HashSet<string> supportedEnvelopeFields = new(StringComparer.Ordinal)
    {
        "eventId",
        "kind",
        "occurredAtUtc",
        "appId",
        "sessionId",
        "correlationId",
        "causationId"
    };

    private static readonly HashSet<string> supportedOperators = new(StringComparer.Ordinal)
    {
        "equals",
        "notEquals",
        "contains",
        "startsWith",
        "endsWith",
        "exists",
        "notExists"
    };

    public RepositoryAutomationTriggerCondition(
        string field,
        string conditionOperator,
        JsonNode? value,
        bool ignoreCase)
    {
        Field = field;
        Operator = conditionOperator;
        Value = value?.DeepClone();
        IgnoreCase = ignoreCase;
    }

    public string Field { get; }

    public string Operator { get; }

    public JsonNode? Value { get; }

    public bool IgnoreCase { get; }

    public static bool IsSupportedOperator(string value) => supportedOperators.Contains(value);

    public static bool IsSupportedField(string value)
    {
        if (supportedEnvelopeFields.Contains(value))
        {
            return true;
        }

        const string payloadPrefix = "payload.";
        return value.Length <= 256
               && value.StartsWith(payloadPrefix, StringComparison.Ordinal)
               && value.Length > payloadPrefix.Length
               && value[payloadPrefix.Length..]
                   .Split('.', StringSplitOptions.None)
                   .All(segment => segment.Length > 0 && segment.Length <= 120);
    }

    public static bool IsStringValue(JsonNode? value) => TryReadString(value, out _);

    public bool Matches(AutomationEventEnvelope envelope)
    {
        var exists = TryResolveField(envelope, out var actual);
        if (string.Equals(Operator, "exists", StringComparison.Ordinal))
        {
            return exists;
        }

        if (string.Equals(Operator, "notExists", StringComparison.Ordinal))
        {
            return !exists;
        }

        if (!exists)
        {
            return false;
        }

        if (string.Equals(Operator, "equals", StringComparison.Ordinal))
        {
            return ValuesEqual(actual, Value);
        }

        if (string.Equals(Operator, "notEquals", StringComparison.Ordinal))
        {
            return !ValuesEqual(actual, Value);
        }

        if (!TryReadString(actual, out var actualText) || !TryReadString(Value, out var expectedText))
        {
            return false;
        }

        var comparison = IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return Operator switch
        {
            "contains" => actualText.Contains(expectedText, comparison),
            "startsWith" => actualText.StartsWith(expectedText, comparison),
            "endsWith" => actualText.EndsWith(expectedText, comparison),
            _ => false
        };
    }

    private bool ValuesEqual(JsonNode? actual, JsonNode? expected)
    {
        if (IgnoreCase
            && TryReadString(actual, out var actualText)
            && TryReadString(expected, out var expectedText))
        {
            return string.Equals(actualText, expectedText, StringComparison.OrdinalIgnoreCase);
        }

        return JsonNode.DeepEquals(actual, expected);
    }

    private bool TryResolveField(AutomationEventEnvelope envelope, out JsonNode? value)
    {
        value = Field switch
        {
            "eventId" => JsonValue.Create(envelope.EventId),
            "kind" => JsonValue.Create(envelope.Kind),
            "occurredAtUtc" => JsonValue.Create(envelope.OccurredAtUtc),
            "appId" => JsonValue.Create(envelope.AppId),
            "sessionId" => JsonValue.Create(envelope.SessionId),
            "correlationId" => JsonValue.Create(envelope.CorrelationId),
            "causationId" => JsonValue.Create(envelope.CausationId),
            _ => null
        };

        if (value is not null || IsKnownNullableTopLevelField(Field))
        {
            return value is not null;
        }

        const string payloadPrefix = "payload.";
        if (!Field.StartsWith(payloadPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        JsonNode? current = envelope.Payload;
        foreach (var segment in Field[payloadPrefix.Length..].Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is not JsonObject currentObject
                || !currentObject.TryGetPropertyValue(segment, out current))
            {
                value = null;
                return false;
            }
        }

        value = current;
        return true;
    }

    private static bool IsKnownNullableTopLevelField(string field)
        => field is "sessionId" or "causationId";

    private static bool TryReadString(JsonNode? node, out string value)
    {
        if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
