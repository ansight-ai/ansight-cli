using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Tasks;

internal static class RepositoryTaskCallTrace
{
    internal const int MaximumArgumentCharacters = 32_768;
    internal const int MaximumResultCharacters = 262_144;

    public static RepositoryTaskCallPayload CaptureArguments(string toolName, JsonNode? arguments, string? appToolId = null)
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

        return Capture(capturedArguments, MaximumArgumentCharacters);
    }

    public static RepositoryTaskCallPayload CaptureResult(JsonNode? result, string? appToolId = null)
    {
        var captured = result?.DeepClone();
        if (string.Equals(appToolId, "clipboard.get_text", StringComparison.Ordinal)
            && captured is JsonObject resultObject
            && resultObject["payload"] is JsonObject payload)
        {
            RedactText(payload["result"] as JsonObject);
        }
        return Capture(captured, MaximumResultCharacters);
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
