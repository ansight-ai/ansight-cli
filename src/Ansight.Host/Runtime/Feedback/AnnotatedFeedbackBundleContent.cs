using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Feedback;

internal sealed class AnnotatedFeedbackBundleContent
{
    private static readonly TimeSpan annotationTimePadding = TimeSpan.FromSeconds(1);

    public required string AnnotationId { get; init; }
    public string? CaptureGroupId { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public string? Feedback { get; init; }
    public IReadOnlyList<AnnotatedFeedbackShape> Shapes { get; init; } = Array.Empty<AnnotatedFeedbackShape>();
    public AnnotatedFeedbackScreenshot? Screenshot { get; init; }
    public IReadOnlyList<AnnotatedFeedbackVisualTree> VisualTrees { get; init; } = Array.Empty<AnnotatedFeedbackVisualTree>();
    public IReadOnlyList<AnnotatedFeedbackArtifact> Artifacts { get; init; } = Array.Empty<AnnotatedFeedbackArtifact>();
    public JsonObject? CustomData { get; init; }
    public IReadOnlyList<SessionAnnotationEvidence> Evidence { get; init; } = Array.Empty<SessionAnnotationEvidence>();
    public IReadOnlyList<string> HookFailures { get; init; } = Array.Empty<string>();

    public string ScreenshotFrameId => $"annotated-feedback-{AnnotationId}";

    public SessionImageFrame? CreateScreenshotFrame(string? frameId = null)
    {
        return Screenshot is null
            ? null
            : new SessionImageFrame
            {
                FrameId = frameId ?? ScreenshotFrameId,
                CapturedAtUtc = Screenshot.CapturedAtUtc,
                Format = Screenshot.Format,
                Width = Screenshot.Width,
                Height = Screenshot.Height,
                Quality = 90,
                ByteCount = Screenshot.Bytes.LongLength
            };
    }

    public SessionAnnotation CreateAnnotation(string? screenshotFrameId)
    {
        var label = Feedback?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
                    ?? "Annotated feedback";
        if (label.Length > 120)
        {
            label = $"{label[..117].TrimEnd()}...";
        }

        var geometry = string.IsNullOrWhiteSpace(screenshotFrameId)
            ? Array.Empty<SessionAnnotationGeometry>()
            : Shapes.Select((shape, index) => new SessionAnnotationGeometry
            {
                GeometryId = $"{AnnotationId}-shape-{index:D3}",
                FrameId = screenshotFrameId,
                CapturedAtUtc = Screenshot?.CapturedAtUtc ?? CapturedAtUtc,
                Kind = shape.Kind switch
                {
                    "ellipse" => SessionAnnotationGeometryKind.Ellipse,
                    "freeDraw" => SessionAnnotationGeometryKind.FreeDraw,
                    _ => SessionAnnotationGeometryKind.Rectangle
                },
                X = shape.X,
                Y = shape.Y,
                Width = shape.Width,
                Height = shape.Height,
                Points = shape.Points.Select(point => new SessionAnnotationGeometryPoint
                {
                    X = point.X,
                    Y = point.Y
                }).ToArray(),
                Text = shape.Text,
                StrokeColor = shape.StrokeColor,
                StrokeWidth = shape.StrokeWidth
            }).ToArray();

        return new SessionAnnotation
        {
            AnnotationId = AnnotationId,
            StartUtc = CapturedAtUtc.Subtract(annotationTimePadding),
            EndUtc = CapturedAtUtc.Add(annotationTimePadding),
            Label = label,
            Source = "sdk.annotatedFeedback",
            Notes = Feedback,
            CaptureGroupId = CaptureGroupId,
            CustomData = CustomData?.DeepClone() as JsonObject,
            Evidence = Evidence.Select(SessionSnapshotCloner.CloneAnnotationEvidence).ToArray(),
            HookFailures = HookFailures.ToArray(),
            Geometry = geometry
        };
    }

    public IReadOnlyList<SessionVisualTreeSnapshot> CreateVisualTreeSnapshots(string? screenshotFrameId)
    {
        return VisualTrees.Select(tree =>
        {
            var payload = tree.Payload.DeepClone() as JsonObject ?? new JsonObject();
            return new SessionVisualTreeSnapshot
            {
                SnapshotId = tree.SnapshotId,
                CapturedAtUtc = tree.CapturedAtUtc,
                VisualTreeKind = ReadPayloadString(payload, "kind", "visualTreeKind") ?? tree.Source,
                VisualTreeFormat = ReadPayloadString(payload, "schema", "format") ?? string.Empty,
                RuntimePlatform = ReadPayloadString(payload, "platform", "runtimePlatform") ?? string.Empty,
                Source = $"sdk.annotatedFeedback.{tree.Source}",
                RootScope = ReadPayloadString(payload, "rootScope") ?? string.Empty,
                MaxDepth = ReadPayloadInt(payload, "maxDepth"),
                IncludeProperties = ReadPayloadBool(payload, "includeProperties"),
                IncludeBindableProperties = ReadPayloadBool(payload, "includeBindableProperties"),
                NodeCount = ReadPayloadInt(payload, "nodeCount"),
                Truncated = tree.Truncated || ReadPayloadBool(payload, "truncated"),
                ScreenshotFrameId = screenshotFrameId,
                ScreenshotCapturedAtUtc = string.IsNullOrWhiteSpace(screenshotFrameId) ? null : Screenshot?.CapturedAtUtc,
                Payload = payload
            };
        }).ToArray();
    }

    public SessionArtifactSnapshot? CreateArtifactSnapshot()
    {
        var captured = Artifacts.Where(artifact => artifact.Bytes is { Length: > 0 }).ToArray();
        if (captured.Length == 0)
        {
            return null;
        }

        var directoryName = $"annotated-feedback-{AnnotationId}";
        return new SessionArtifactSnapshot
        {
            SnapshotId = directoryName,
            CapturedAtUtc = CapturedAtUtc,
            Source = "sdk.annotatedFeedback",
            RootAlias = "annotated-feedback",
            RootPath = AnnotationId,
            RelativePath = AnnotationId,
            Name = "Annotated feedback artifacts",
            Kind = "annotated-feedback",
            ArtifactDirectoryName = directoryName,
            FileCount = captured.Length,
            ByteCount = captured.Sum(artifact => artifact.Bytes!.LongLength),
            Entries = captured.Select(artifact => new SessionArtifactEntry
            {
                Name = artifact.Name,
                RootAlias = "annotated-feedback",
                RelativePath = artifact.FileName,
                SnapshotRelativePath = artifact.FileName,
                Kind = artifact.Kind,
                SizeBytes = artifact.Bytes!.LongLength,
                FileExtension = Path.GetExtension(artifact.FileName),
                MimeType = artifact.MimeType,
                LastModifiedUtc = CapturedAtUtc.ToString("O"),
                ArchiveRelativePath = $"{directoryName}/{artifact.FileName}"
            }).ToArray()
        };
    }

    private static string? ReadPayloadString(JsonObject payload, params string[] names)
    {
        foreach (var name in names)
        {
            if (payload[name] is JsonValue value && value.TryGetValue<string>(out var result) && !string.IsNullOrWhiteSpace(result))
            {
                return result.Trim();
            }
        }

        return null;
    }

    private static int ReadPayloadInt(JsonObject payload, string name)
        => payload[name] is JsonValue value && value.TryGetValue<int>(out var result) ? Math.Max(0, result) : 0;

    private static bool ReadPayloadBool(JsonObject payload, string name)
        => payload[name] is JsonValue value && value.TryGetValue<bool>(out var result) && result;
}
