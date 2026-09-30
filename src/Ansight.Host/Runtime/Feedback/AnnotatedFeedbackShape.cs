using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Feedback;

internal sealed record AnnotatedFeedbackShape(
    string Kind,
    double X,
    double Y,
    double Width,
    double Height,
    IReadOnlyList<AnnotatedFeedbackPoint> Points,
    string? Text,
    string? StrokeColor,
    double? StrokeWidth);
