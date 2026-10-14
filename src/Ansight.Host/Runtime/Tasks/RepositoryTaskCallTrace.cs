using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Tasks;

internal static class RepositoryTaskCallTrace
{
    internal const int MaximumArgumentCharacters = 32_768;
    internal const int MaximumResultCharacters = 262_144;

    public static RepositoryTaskAssertion CaptureAssertion(RepositoryTaskAssertion assertion)
        => assertion with
        {
            Expected = CaptureAssertionValue(assertion.Expected),
            Actual = CaptureAssertionValue(assertion.Actual)
        };

    private static JsonNode? CaptureAssertionValue(JsonNode? value)
    {
        var payload = Capture(value, MaximumArgumentCharacters);
        return payload.WasTruncated ? new JsonObject
        {
            ["traceValueTruncated"] = true,
            ["preview"] = payload.Content,
            ["originalCharacterCount"] = payload.OriginalCharacterCount,
            ["sha256"] = payload.Sha256
        } : value?.DeepClone();
    }

    public static RepositoryTaskCallPayload CaptureArguments(string toolName, JsonNode? arguments, string? appToolId = null,
        IEnumerable<string>? secretValues = null)
    {
        var capturedArguments = arguments?.DeepClone() ?? new JsonObject();
        if (string.Equals(toolName, "ansight_type_text", StringComparison.Ordinal)
            && capturedArguments is JsonObject argumentObject
            && argumentObject["value"] is JsonValue value
            && value.TryGetValue<string>(out var text))
        {
            argumentObject["value"] = $"<redacted {text.Length} character(s)>";
        }

        var clipboardToolId = appToolId;
        if (clipboardToolId is null && capturedArguments is JsonObject argumentsObject
            && argumentsObject["toolId"] is JsonValue toolIdValue
            && toolIdValue.TryGetValue<string>(out var toolIdText))
        {
            clipboardToolId = toolIdText;
        }
        if (string.Equals(clipboardToolId, "clipboard.set_text", StringComparison.Ordinal))
        {
            var values = capturedArguments as JsonObject;
            RedactText(values?["arguments"] as JsonObject ?? values);
        }

        RedactSecrets(capturedArguments, secretValues);
        return Capture(capturedArguments, MaximumArgumentCharacters);
    }

    public static RepositoryTaskCallPayload CaptureResult(JsonNode? result, string? appToolId = null,
        IEnumerable<string>? secretValues = null)
    {
        var captured = result?.DeepClone();
        if (string.Equals(appToolId, "clipboard.get_text", StringComparison.Ordinal)
            && captured is JsonObject resultObject
            && resultObject["payload"] is JsonObject payload)
        {
            RedactText(payload["result"] as JsonObject);
        }
        RedactSecrets(captured, secretValues);
        return Capture(captured, MaximumResultCharacters);
    }

    private static void RedactSecrets(JsonNode? node, IEnumerable<string>? values)
    {
        var secrets = values?.Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length).ToArray() ?? [];
        if (secrets.Length == 0 || node is null) return;
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
            {
                if (property.Value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                {
                    foreach (var secret in secrets) text = text.Replace(secret, "<redacted secret>", StringComparison.Ordinal);
                    obj[property.Key] = text;
                }
                else RedactSecrets(property.Value, secrets);
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                {
                    foreach (var secret in secrets) text = text.Replace(secret, "<redacted secret>", StringComparison.Ordinal);
                    array[index] = text;
                }
                else RedactSecrets(array[index], secrets);
            }
        }
    }

    private static void RedactText(JsonObject? value)
    {
        if (value?["text"] is JsonValue textNode && textNode.TryGetValue<string>(out var text))
            value["text"] = $"<redacted {text.Length} character(s)>";
    }

    private static RepositoryTaskCallPayload Capture(JsonNode? value, int maximumCharacters)
    {
        string content;
        try
        {
            content = value?.ToJsonString(JsonUtil.Compact) ?? "null";
        }
        catch (Exception exception) when (exception is JsonException
                                           or InvalidOperationException
                                           or NotSupportedException
                                           or ArgumentException)
        {
            // A diagnostic capture must never change whether an API call succeeds.
            content = new JsonObject
            {
                ["captureError"] = "The call payload could not be serialized.",
                ["exceptionType"] = exception.GetType().Name
            }.ToJsonString(JsonUtil.Compact);
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        var retainedLength = Math.Min(content.Length, maximumCharacters);
        if (retainedLength < content.Length && char.IsHighSurrogate(content[retainedLength - 1]))
        {
            retainedLength--;
        }

        return new RepositoryTaskCallPayload(
            content[..retainedLength],
            content.Length,
            retainedLength < content.Length,
            hash);
    }
}
