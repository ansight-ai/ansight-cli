using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Ansight.Host.AppGraphs;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal static class NavigationStructureObservation
{
    private const int MaximumDepth = 14;
    private const int MaximumNodes = 220;
    private const int MaximumStringCharacters = 320;

    public static JsonObject? Build(JsonObject structuredContent)
    {
        var toolId = ReadString(structuredContent, "toolId");
        var selectedFramework = ReadString(structuredContent, "selectedNavigationController");
        var controller = NavigationControllerCatalog.ResolveFramework(selectedFramework)
                         ?? NavigationControllerCatalog.ResolveNavigationTool(toolId);
        var source = structuredContent["payload"]?["result"]
                     ?? structuredContent["payload"];
        if (controller is null || source is null)
        {
            return null;
        }

        var remainingNodes = MaximumNodes;
        var state = Compact(source, 0, ref remainingNodes);
        if (state is null)
        {
            return null;
        }
        UiNodeProjection.CompactBoundsInPlace(state);

        var serializedState = state.ToJsonString();
        var fingerprint = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(serializedState)))
            .ToLowerInvariant();
        return new JsonObject
        {
            ["capability"] = "navigation.structure.observe",
            ["sessionId"] = structuredContent["sessionId"]?.DeepClone(),
            ["appId"] = structuredContent["appId"]?.DeepClone(),
            ["framework"] = controller.Framework,
            ["navigationToolId"] = toolId,
            ["visualTreeToolIds"] = new JsonArray(
                controller.VisualTreeToolIds.Select(static value => (JsonNode?)value).ToArray()),
            ["selectedNavigationController"] = structuredContent["selectedNavigationController"]?.DeepClone()
                                               ?? JsonValue.Create(controller.Framework),
            ["availableNavigationControllers"] = CompactControllers(structuredContent["availableNavigationControllers"])
                                                 ?? new JsonArray
                                                 {
                                                     new JsonObject
                                                     {
                                                         ["framework"] = controller.Framework,
                                                         ["navigationToolId"] = toolId,
                                                         ["technologyKinds"] = AppGraphNavigationTechnologyCatalog.BuildKindsJson(controller.Framework)
                                                     }
                                                 },
            ["structureFingerprint"] = fingerprint,
            ["truncated"] = remainingNodes == 0,
            ["state"] = state
        };
    }

    private static JsonNode? CompactControllers(JsonNode? source)
    {
        var result = source?.DeepClone();
        if (result is JsonArray controllers)
        {
            foreach (var controller in controllers.OfType<JsonObject>())
            {
                controller.Remove("guidance");
            }
        }
        return result;
    }

    private static JsonNode? Compact(JsonNode? source, int depth, ref int remainingNodes)
    {
        if (source is null || depth > MaximumDepth || remainingNodes <= 0)
        {
            remainingNodes = Math.Max(0, remainingNodes);
            return null;
        }

        remainingNodes--;
        if (source is JsonObject sourceObject)
        {
            var result = new JsonObject();
            foreach (var property in sourceObject.Take(80))
            {
                if (ShouldOmit(property.Key))
                {
                    continue;
                }

                var compactedProperty = Compact(property.Value, depth + 1, ref remainingNodes);
                if (compactedProperty is not null)
                {
                    result[property.Key] = compactedProperty;
                }
                if (remainingNodes <= 0)
                {
                    break;
                }
            }
            return result;
        }

        if (source is JsonArray sourceArray)
        {
            var result = new JsonArray();
            foreach (var item in sourceArray.Take(80))
            {
                var compactedItem = Compact(item, depth + 1, ref remainingNodes);
                if (compactedItem is not null)
                {
                    result.Add(compactedItem);
                }
                if (remainingNodes <= 0)
                {
                    break;
                }
            }
            return result;
        }

        if (source is JsonValue scalarValue
            && scalarValue.TryGetValue<string>(out var text))
        {
            return JsonValue.Create(text.Length <= MaximumStringCharacters
                ? text
                : $"{text[..(MaximumStringCharacters - 1)]}…");
        }

        return source.DeepClone();
    }

    private static bool ShouldOmit(string propertyName)
        => propertyName.Contains("screenshot", StringComparison.OrdinalIgnoreCase)
           || propertyName.Contains("base64", StringComparison.OrdinalIgnoreCase)
           || propertyName.Contains("debug", StringComparison.OrdinalIgnoreCase)
           || propertyName.Contains("stackTrace", StringComparison.OrdinalIgnoreCase)
           || propertyName.Contains("timestamp", StringComparison.OrdinalIgnoreCase)
           || propertyName.EndsWith("AtUtc", StringComparison.OrdinalIgnoreCase)
           || propertyName.Equals("requestId", StringComparison.OrdinalIgnoreCase)
           || propertyName.Equals("correlationId", StringComparison.OrdinalIgnoreCase)
           || propertyName.Equals("durationMilliseconds", StringComparison.OrdinalIgnoreCase);

    private static string? ReadString(JsonObject value, string propertyName)
        => value[propertyName] is JsonValue stringValue
           && stringValue.TryGetValue<string>(out var text)
            ? text
            : null;
}
