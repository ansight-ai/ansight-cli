using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Feedback;

internal sealed record AnnotatedFeedbackScreenshot(
    string Format,
    int Width,
    int Height,
    DateTimeOffset CapturedAtUtc,
    byte[] Bytes);
