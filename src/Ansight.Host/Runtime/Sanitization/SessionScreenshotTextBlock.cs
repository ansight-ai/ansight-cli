using System.Diagnostics;
using System.Globalization;

namespace Ansight.Host.Runtime.Sanitization;

internal sealed record SessionScreenshotTextBlock(
    string Text,
    double Confidence,
    SessionSanitizationRegion Bounds);
