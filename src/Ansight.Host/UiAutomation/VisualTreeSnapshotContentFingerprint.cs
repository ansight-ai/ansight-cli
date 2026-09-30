namespace Ansight.Host.UiAutomation;

using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host;

internal static class VisualTreeSnapshotContentFingerprint
{
    private static readonly HashSet<string> ignoredRootPayloadProperties =
    [
        "capturedAtUtc",
        "captureTrigger"
    ];

    public static bool RepresentsSameVisualTree(
        SessionVisualTreeSnapshot left,
        SessionVisualTreeSnapshot right)
    {
        return string.Equals(left.VisualTreeKind, right.VisualTreeKind, StringComparison.Ordinal)
               && string.Equals(left.VisualTreeFormat, right.VisualTreeFormat, StringComparison.Ordinal)
               && string.Equals(left.RuntimePlatform, right.RuntimePlatform, StringComparison.Ordinal)
               && string.Equals(left.RootScope, right.RootScope, StringComparison.Ordinal);
    }

    public static string Compute(SessionVisualTreeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("visualTreeKind", snapshot.VisualTreeKind);
            writer.WriteString("visualTreeFormat", snapshot.VisualTreeFormat);
            writer.WriteString("runtimePlatform", snapshot.RuntimePlatform);
            writer.WriteString("rootScope", snapshot.RootScope);
            writer.WriteNumber("maxDepth", snapshot.MaxDepth);
            writer.WriteBoolean("includeProperties", snapshot.IncludeProperties);
            writer.WriteBoolean("includeBindableProperties", snapshot.IncludeBindableProperties);
            writer.WriteNumber("nodeCount", snapshot.NodeCount);
            writer.WriteBoolean("truncated", snapshot.Truncated);
            writer.WritePropertyName("payload");
            WriteCanonicalNode(writer, snapshot.Payload, isPayloadRoot: true);
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    private static void WriteCanonicalNode(Utf8JsonWriter writer, JsonNode? node, bool isPayloadRoot = false)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                return;
            case JsonObject jsonObject:
                writer.WriteStartObject();
                foreach (var property in jsonObject.OrderBy(property => property.Key, StringComparer.Ordinal))
                {
                    if (isPayloadRoot && ignoredRootPayloadProperties.Contains(property.Key))
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Key);
                    WriteCanonicalNode(writer, property.Value);
                }

                writer.WriteEndObject();
                return;
            case JsonArray jsonArray:
                writer.WriteStartArray();
                foreach (var item in jsonArray)
                {
                    WriteCanonicalNode(writer, item);
                }

                writer.WriteEndArray();
                return;
            default:
                node.WriteTo(writer, JsonUtil.Compact);
                return;
        }
    }
}
