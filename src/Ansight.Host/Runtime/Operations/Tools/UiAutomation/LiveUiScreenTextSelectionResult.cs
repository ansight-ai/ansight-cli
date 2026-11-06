namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed record LiveUiScreenTextSelectionResult(
    IReadOnlyList<SessionScreenshotTextBlock> Blocks, int TotalCount, int RawCount);
