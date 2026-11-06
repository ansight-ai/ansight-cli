using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed record LiveUiCaptureResult(
    bool IsSuccess,
    LiveUiTreeCapture? Capture,
    string Message)
{
    public IReadOnlyList<string> AttemptedToolIds { get; init; } = [];

    public static LiveUiCaptureResult Success(LiveUiTreeCapture capture)
        => new(true, capture, string.Empty)
        {
            AttemptedToolIds = [capture.ToolId]
        };

    public static LiveUiCaptureResult Failure(string message)
        => new(false, null, message);
}
