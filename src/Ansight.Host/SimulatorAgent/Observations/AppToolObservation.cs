using System.Text.Json.Nodes;

namespace Ansight.Host.SimulatorAgent.Observations;

internal static class AppToolObservation
{
    private const int MaximumAnnotationCharacters = 8_000;

    public static JsonObject Build(JsonObject content)
    {
        var toolId = ReadString(content, "toolId");
        if (toolId is null
            || content["payload"] is not JsonObject payload
            || !ReadBoolean(payload, "success")
            || payload["result"] is not JsonObject result)
        {
            return content;
        }

        JsonObject? compactResult = result switch
        {
            _ when IsAnnotationSurfaceResult(result) => BuildAnnotationSurfaceResult(result),
            _ when result["players"] is JsonArray => BuildPlayerListResult(result),
            _ when result["player"] is JsonObject || result["scene"] is JsonObject =>
                BuildPlayerQueryResult(result),
            _ => null
        };
        if (compactResult is null)
        {
            return content;
        }

        return new JsonObject
        {
            ["sessionId"] = content["sessionId"]?.DeepClone(),
            ["appId"] = content["appId"]?.DeepClone(),
            ["toolId"] = content["toolId"]?.DeepClone(),
            ["responseType"] = content["responseType"]?.DeepClone(),
            ["payload"] = new JsonObject
            {
                ["toolId"] = payload["toolId"]?.DeepClone(),
                ["success"] = payload["success"]?.DeepClone(),
                ["message"] = payload["message"]?.DeepClone(),
                ["result"] = compactResult
            }
        };
    }

    private static bool IsAnnotationSurfaceResult(JsonObject result)
        => result["annotationManagers"] is JsonArray
           || result["boundAnnotations"] is JsonArray
           || (result["surface"] is JsonObject surface
               && (surface["annotationManagerCount"] is not null
                   || surface["annotationCount"] is not null));

    private static JsonObject BuildAnnotationSurfaceResult(JsonObject result)
    {
        var compactResult = new JsonObject();
        Copy(result, compactResult, "platform");
        Copy(result, compactResult, "capturedAtUtc");
        Copy(result, compactResult, "query");
        Copy(result, compactResult, "annotationManagerCount");
        Copy(result, compactResult, "annotationCount");
        Copy(result, compactResult, "returnedAnnotationCount");
        Copy(result, compactResult, "truncated");
        Copy(result, compactResult, "boundAnnotationCount");

        if (result["surface"] is JsonObject surface)
        {
            compactResult["surface"] = Select(
                surface,
                "surfaceId",
                "tag",
                "ownerTag",
                "type",
                "automationId",
                "visible",
                "enabled",
                "handlerAttached",
                "queryable",
                "camera",
                "region",
                "viewportExtents",
                "annotationManagerCount",
                "annotationCount");
        }

        if (result["annotationManagers"] is JsonArray managers)
        {
            var projection = BuildAnnotationManagers(
                managers,
                BuildSurfaceSelector(result["surface"] as JsonObject));
            compactResult["annotationManagers"] = projection.Managers;
            compactResult["projectionReturnedAnnotationCount"] = projection.ReturnedAnnotationCount;
            compactResult["projectionTruncated"] = projection.WasTruncated;
        }

        return compactResult;
    }

    private static AnnotationProjection BuildAnnotationManagers(
        JsonArray managers,
        JsonObject? surfaceSelector)
    {
        var compactManagers = new JsonArray();
        var characterCount = 0;
        var sourceAnnotationCount = managers
            .OfType<JsonObject>()
            .Sum(static manager => manager["annotations"] is JsonArray annotations ? annotations.Count : 0);
        var returnedAnnotationCount = 0;
        foreach (var manager in managers.OfType<JsonObject>())
        {
            var compactManager = Select(
                manager,
                "id",
                "sourceId",
                "layerId",
                "annotationCount",
                "returnedCount");
            var compactAnnotations = new JsonArray();
            var sourceManagerAnnotationCount = 0;
            if (manager["annotations"] is JsonArray annotations)
            {
                sourceManagerAnnotationCount = annotations.Count;
                foreach (var annotation in annotations.OfType<JsonObject>())
                {
                    var compactAnnotation = Select(
                        annotation,
                        "id",
                        "kind",
                        "screenPosition",
                        "selected");
                    if (annotation["geometry"] is JsonObject geometry
                        && string.Equals(ReadString(geometry, "type"), "Point", StringComparison.Ordinal))
                    {
                        compactAnnotation["coordinates"] = geometry["coordinates"]?.DeepClone();
                    }

                    if (annotation["properties"] is JsonObject properties)
                    {
                        CopyFirst(properties, compactAnnotation, "label", "text-field", "name", "title");
                    }

                    if (surfaceSelector is not null
                        && annotation["screenPosition"] is JsonObject screenPosition
                        && TryReadDouble(screenPosition, "x", out var targetX)
                        && TryReadDouble(screenPosition, "y", out var targetY)
                        && targetX >= 0
                        && targetY >= 0)
                    {
                        compactAnnotation["tapHint"] = new JsonObject
                        {
                            ["tool"] = "ansight_tap_ui",
                            ["selector"] = surfaceSelector.DeepClone(),
                            ["targetX"] = targetX,
                            ["targetY"] = targetY
                        };
                    }

                    var annotationCharacters = compactAnnotation.ToJsonString().Length;
                    if (characterCount + annotationCharacters > MaximumAnnotationCharacters)
                    {
                        break;
                    }

                    compactAnnotations.Add(compactAnnotation);
                    characterCount += annotationCharacters;
                    returnedAnnotationCount++;
                }
            }

            compactManager["annotations"] = compactAnnotations;
            compactManager["projectionSourceCount"] = sourceManagerAnnotationCount;
            compactManager["projectionReturnedCount"] = compactAnnotations.Count;
            compactManager["projectionTruncated"] = compactAnnotations.Count < sourceManagerAnnotationCount;
            compactManagers.Add(compactManager);
            if (characterCount >= MaximumAnnotationCharacters)
            {
                break;
            }
        }

        return new AnnotationProjection(
            compactManagers,
            returnedAnnotationCount,
            returnedAnnotationCount < sourceAnnotationCount);
    }

    private static JsonObject? BuildSurfaceSelector(JsonObject? surface)
    {
        if (surface is null
            || !ReadBoolean(surface, "visible")
            || !ReadBoolean(surface, "enabled"))
        {
            return null;
        }

        var automationId = ReadString(surface, "automationId");
        var type = ReadString(surface, "type");
        if (automationId is null && type is null)
        {
            return null;
        }

        var selector = new JsonObject
        {
            ["visible"] = true,
            ["enabled"] = true
        };
        if (automationId is not null)
        {
            selector["automationId"] = automationId;
        }
        else if (type is not null)
        {
            selector["type"] = type[(type.LastIndexOf('.') + 1)..];
        }

        return selector;
    }

    private static JsonObject BuildPlayerListResult(JsonObject result)
    {
        var compactResult = Select(
            result,
            "platform",
            "capturedAtUtc",
            "matchCount",
            "returnedCount",
            "truncated");
        if (result["players"] is JsonArray players)
        {
            compactResult["players"] = new JsonArray(
                players.OfType<JsonObject>().Select(BuildPlayerSummary).ToArray());
        }

        return compactResult;
    }

    private static JsonObject BuildPlayerQueryResult(JsonObject result)
    {
        var compactResult = Select(result, "platform", "capturedAtUtc", "requestedElement");
        if (result["player"] is JsonObject player)
        {
            compactResult["player"] = BuildPlayerSummary(player);
        }

        if (result["scene"] is JsonObject scene)
        {
            var compactScene = Select(scene, "initialized", "type", "entitiesByTag");
            if (scene["asset"] is JsonObject asset)
            {
                compactScene["asset"] = Select(asset, "hasLoadedAsset");
            }

            if (scene["camera"] is JsonObject camera
                && camera["state"] is JsonObject cameraState)
            {
                compactScene["cameraState"] = Select(cameraState, "position", "orientation");
            }

            compactResult["scene"] = compactScene;
        }

        return compactResult;
    }

    private static JsonObject BuildPlayerSummary(JsonObject player)
    {
        var compactPlayer = Select(
            player,
            "playerId",
            "tag",
            "ownerTag",
            "type",
            "pageType",
            "visible",
            "enabled",
            "handlerAttached",
            "paused",
            "bounds");
        if (player["application"] is JsonObject application)
        {
            compactPlayer["application"] = Select(application, "type", "evergineVersion", "sceneInitialized");
        }

        if (player["guide"] is JsonObject guide)
        {
            var compactGuide = Select(
                guide,
                "viewModelAttached",
                "area",
                "assetDefinition",
                "license",
                "loading",
                "selection",
                "visibility",
                "counts");
            if (guide["camera"] is JsonObject camera
                && camera["state"] is JsonObject cameraState)
            {
                compactGuide["cameraState"] = Select(cameraState, "position", "orientation");
            }

            compactPlayer["guide"] = compactGuide;
        }

        var playerType = ReadString(player, "type");
        var ownerTag = ReadString(player, "ownerTag");
        if (ReadBoolean(player, "visible")
            && ReadBoolean(player, "enabled")
            && playerType is not null)
        {
            var shortPlayerType = playerType[(playerType.LastIndexOf('.') + 1)..];
            var selector = new JsonObject
            {
                ["type"] = shortPlayerType,
                ["visible"] = true,
                ["enabled"] = true
            };
            if (ownerTag is not null)
            {
                selector["ancestorAutomationId"] = ownerTag;
            }

            compactPlayer["swipeHint"] = new JsonObject
            {
                ["tool"] = "ansight_swipe_ui",
                ["selector"] = selector,
                ["orientation"] = "W",
                ["length"] = 0.35,
                ["durationMs"] = 400
            };
        }

        return compactPlayer;
    }

    private static JsonObject Select(JsonObject source, params string[] propertyNames)
    {
        var result = new JsonObject();
        foreach (var propertyName in propertyNames)
        {
            Copy(source, result, propertyName);
        }

        return result;
    }

    private static void Copy(JsonObject source, JsonObject target, string propertyName)
    {
        if (source[propertyName] is { } value)
        {
            target[propertyName] = value.DeepClone();
        }
    }

    private static void CopyFirst(
        JsonObject source,
        JsonObject target,
        string targetPropertyName,
        params string[] sourcePropertyNames)
    {
        foreach (var propertyName in sourcePropertyNames)
        {
            if (source[propertyName] is not { } value)
            {
                continue;
            }

            target[targetPropertyName] = value.DeepClone();
            return;
        }
    }

    private static string? ReadString(JsonObject value, string propertyName)
        => value[propertyName] is JsonValue property
           && property.TryGetValue<string>(out var text)
           && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static bool ReadBoolean(JsonObject value, string propertyName)
        => value[propertyName] is JsonValue property
           && property.TryGetValue<bool>(out var result)
           && result;

    private static bool TryReadDouble(JsonObject value, string propertyName, out double result)
    {
        result = 0;
        if (value[propertyName] is not JsonValue property)
        {
            return false;
        }

        if (property.TryGetValue<double>(out result))
        {
            return double.IsFinite(result);
        }

        if (property.TryGetValue<int>(out var integer))
        {
            result = integer;
            return true;
        }

        if (property.TryGetValue<long>(out var longInteger))
        {
            result = longInteger;
            return true;
        }

        return false;
    }
}
