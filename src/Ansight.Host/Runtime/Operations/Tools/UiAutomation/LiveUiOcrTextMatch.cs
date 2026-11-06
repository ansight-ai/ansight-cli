using System.Text.Json.Nodes;
using SkiaSharp;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed record LiveUiOcrTextMatch(
    SessionScreenshotTextBlock Block,
    LiveUiSelectorMatchResult Evaluation);
