using System.Text.Json.Nodes;
using Ansight.Tools;
using SkiaSharp;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed record ScreenshotDifference(
    int Width,
    int Height,
    long DifferentPixelCount,
    long TotalPixelCount,
    double DifferenceRatio,
    double MeanChannelDifference,
    long IgnoredPixelCount);
