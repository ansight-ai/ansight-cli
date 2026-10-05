using System.Globalization;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal static class AnnotationMutationArgumentReader
{
    public static bool TryRead(
        JsonObject? arguments,
        SessionAnnotationMutationKind kind,
        out SessionAnnotationMutation? mutation,
        out string? error)
    {
        mutation = null;
        try
        {
            var input = arguments ?? new JsonObject();
            var allowed = kind switch
            {
                SessionAnnotationMutationKind.Create => new[] { "annotationId", "label", "notes", "status", "source", "startUtc", "endUtc", "geometries", "target" },
                SessionAnnotationMutationKind.Patch => new[] { "annotationId", "expectedSource", "label", "notes", "status", "startUtc", "endUtc", "geometries", "target" },
                SessionAnnotationMutationKind.Remove => new[] { "annotationId", "expectedSource" },
                _ => throw new InvalidDataException("Unknown annotation mutation.")
            };
            RequireProperties(input, allowed.Concat(["sessionId", "appId", "includeHistorical"]));
            _ = ReadString(input, "sessionId", nullable: true);
            _ = ReadString(input, "appId", nullable: true);
            if (input["includeHistorical"] is not null
                && (input["includeHistorical"] is not JsonValue historical || !historical.TryGetValue<bool>(out _)))
            {
                throw new InvalidDataException("includeHistorical must be a boolean.");
            }
            var isPatch = kind == SessionAnnotationMutationKind.Patch;
            mutation = new SessionAnnotationMutation
            {
                Kind = kind,
                AnnotationId = ReadString(input, "annotationId", required: kind != SessionAnnotationMutationKind.Create),
                ExpectedSource = ReadString(input, "expectedSource", preserveWhitespace: true),
                Label = ReadString(input, "label", required: kind == SessionAnnotationMutationKind.Create),
                Source = ReadString(input, "source"),
                HasNotes = input.ContainsKey("notes"),
                Notes = ReadString(input, "notes", nullable: isPatch, allowEmpty: true),
                HasStatus = input.ContainsKey("status"),
                Status = ReadString(input, "status", nullable: true, allowEmpty: true),
                StartUtc = ReadTimestamp(input, "startUtc"),
                HasEndUtc = input.ContainsKey("endUtc"),
                EndUtc = ReadTimestamp(input, "endUtc", nullable: isPatch),
                Geometries = input.ContainsKey("geometries") ? ReadGeometries(input["geometries"]) : null,
                HasTarget = input.ContainsKey("target"),
                Target = ReadTarget(input, nullable: isPatch)
            };
            error = null;
            return true;
        }
        catch (InvalidDataException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static IReadOnlyList<SessionAnnotationGeometry> ReadGeometries(JsonNode? value)
    {
        if (value is not JsonArray entries)
        {
            throw new InvalidDataException("geometries must be an array; use [] to clear it.");
        }

        var geometries = new List<SessionAnnotationGeometry>();
        var geometryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is not JsonObject geometry)
            {
                throw new InvalidDataException("Each geometry must be an object.");
            }

            RequireProperties(geometry, ["geometryId", "frameId", "kind", "x", "y", "width", "height", "points", "text", "strokeColor", "strokeWidth"]);
            var kindText = ReadString(geometry, "kind", required: true);
            var kind = kindText switch
            {
                "point" => SessionAnnotationGeometryKind.Point,
                "rectangle" => SessionAnnotationGeometryKind.Rectangle,
                "ellipse" => SessionAnnotationGeometryKind.Ellipse,
                "freeDraw" => SessionAnnotationGeometryKind.FreeDraw,
                _ => throw new InvalidDataException("Geometry kind must be point, rectangle, ellipse, or freeDraw.")
            };
            var id = ReadString(geometry, "geometryId") ?? Guid.NewGuid().ToString("N");
            if (!geometryIds.Add(id))
            {
                throw new InvalidDataException($"Geometry id '{id}' occurs more than once.");
            }

            var points = new List<SessionAnnotationGeometryPoint>();
            double x;
            double y;
            double? width = null;
            double? height = null;
            if (kind == SessionAnnotationGeometryKind.FreeDraw)
            {
                RejectProperties(geometry, ["x", "y", "width", "height"]);
                if (geometry["points"] is not JsonArray path || path.Count < 2)
                {
                    throw new InvalidDataException("freeDraw geometry requires at least two points.");
                }
                foreach (var point in path)
                {
                    if (point is not JsonObject pointObject)
                    {
                        throw new InvalidDataException("Each freeDraw point must be an object.");
                    }
                    RequireProperties(pointObject, ["x", "y"]);
                    points.Add(new SessionAnnotationGeometryPoint
                    {
                        X = ReadCoordinate(pointObject, "x"),
                        Y = ReadCoordinate(pointObject, "y")
                    });
                }
                x = points.Min(point => point.X);
                y = points.Min(point => point.Y);
                width = points.Max(point => point.X) - x;
                height = points.Max(point => point.Y) - y;
                if (width == 0 && height == 0)
                {
                    throw new InvalidDataException("freeDraw geometry requires at least two distinct path positions.");
                }
            }
            else
            {
                RejectProperties(geometry, ["points"]);
                x = ReadCoordinate(geometry, "x");
                y = ReadCoordinate(geometry, "y");
                if (kind == SessionAnnotationGeometryKind.Point)
                {
                    RejectProperties(geometry, ["width", "height"]);
                }
                else
                {
                    width = ReadNumber(geometry, "width", required: true);
                    height = ReadNumber(geometry, "height", required: true);
                    if (width <= 0 || height <= 0 || x + width > 1 || y + height > 1)
                    {
                        throw new InvalidDataException("Geometry boxes require positive dimensions and must fit inside the screenshot.");
                    }
                }
            }

            var strokeWidth = ReadNumber(geometry, "strokeWidth");
            if (strokeWidth is <= 0)
            {
                throw new InvalidDataException("strokeWidth must be a positive finite number.");
            }
            var strokeColor = ReadString(geometry, "strokeColor");
            if (strokeColor is not null && !IsHexColor(strokeColor))
            {
                throw new InvalidDataException("strokeColor must be #RRGGBB or #AARRGGBB hexadecimal color.");
            }
            geometries.Add(new SessionAnnotationGeometry
            {
                GeometryId = id,
                FrameId = ReadString(geometry, "frameId", required: true)!,
                CapturedAtUtc = default, // Resolved from the selected session's saved frame inside the mutation lock.
                Kind = kind,
                X = x,
                Y = y,
                Width = width,
                Height = height,
                Points = points,
                Text = ReadString(geometry, "text", allowEmpty: true),
                StrokeColor = strokeColor,
                StrokeWidth = strokeWidth
            });
        }
        return geometries;
    }

    private static SessionAnnotationTargetReference? ReadTarget(JsonObject input, bool nullable)
    {
        if (!input.ContainsKey("target") || nullable && input["target"] is null)
        {
            return null;
        }
        if (input["target"] is not JsonObject target)
        {
            throw new InvalidDataException("target must reference an existing visual-tree snapshot and node.");
        }
        RequireProperties(target, ["visualTreeSnapshotId", "nodeId"]);
        return new SessionAnnotationTargetReference(
            ReadString(target, "visualTreeSnapshotId", required: true)!,
            ReadString(target, "nodeId", required: true)!);
    }

    private static string? ReadString(JsonObject input, string name, bool required = false, bool nullable = false, bool allowEmpty = false, bool preserveWhitespace = false)
    {
        if (!input.ContainsKey(name))
        {
            return required ? throw new InvalidDataException($"{name} is required.") : null;
        }
        if (nullable && input[name] is null)
        {
            return null;
        }
        if (input[name] is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            throw new InvalidDataException($"{name} must be a string{(nullable ? " or null" : string.Empty)}.");
        }
        var normalized = text.Trim();
        if (normalized.Length == 0)
        {
            return allowEmpty ? null : throw new InvalidDataException($"{name} must not be empty.");
        }
        return preserveWhitespace ? text : normalized;
    }

    private static DateTimeOffset? ReadTimestamp(JsonObject input, string name, bool nullable = false)
    {
        var text = ReadString(input, name, nullable: nullable);
        if (text is null)
        {
            return null;
        }
        // Require an explicit timezone; interpreting local wall time would make task placement machine-dependent.
        var timeSeparator = text.IndexOf('T');
        var hasZone = text.EndsWith('Z') || timeSeparator >= 0 && (text.LastIndexOf('+') > timeSeparator || text.LastIndexOf('-') > timeSeparator);
        if (!hasZone || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
        {
            throw new InvalidDataException($"{name} must be an ISO-8601 timestamp with an explicit timezone.");
        }
        return timestamp.ToUniversalTime();
    }

    private static double ReadCoordinate(JsonObject input, string name)
    {
        var value = ReadNumber(input, name, required: true)!.Value;
        if (value is < 0 or > 1)
        {
            throw new InvalidDataException($"{name} must be between 0 and 1.");
        }
        return value;
    }

    private static double? ReadNumber(JsonObject input, string name, bool required = false)
    {
        if (!input.ContainsKey(name) && !required)
        {
            return null;
        }
        if (input[name] is not JsonValue value
            || !(value.TryGetValue<double>(out var number)
                 || value.GetValueKind() == JsonValueKind.Number
                 && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            || !double.IsFinite(number))
        {
            throw new InvalidDataException($"{name} must be a finite number.");
        }
        return number;
    }

    private static bool IsHexColor(string color)
        => color.Length is 7 or 9 && color[0] == '#' && color.AsSpan(1).ToArray().All(Uri.IsHexDigit);

    private static void RequireProperties(JsonObject value, IEnumerable<string> allowed)
    {
        var known = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var property in value)
        {
            if (!known.Contains(property.Key))
            {
                throw new InvalidDataException($"Unknown annotation field '{property.Key}'.");
            }
        }
    }

    private static void RejectProperties(JsonObject value, IEnumerable<string> prohibited)
    {
        foreach (var property in prohibited.Where(value.ContainsKey))
        {
            throw new InvalidDataException($"{property} is not valid for this geometry kind.");
        }
    }
}
