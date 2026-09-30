using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Feedback;

internal sealed record AnnotatedFeedbackVisualTree(
    string SnapshotId,
    string Source,
    string DisplayName,
    DateTimeOffset CapturedAtUtc,
    bool Truncated,
    JsonObject Payload);
