using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.UiAutomation;

public sealed record AppInteractionScreenshot(
    string FrameId,
    string ArtifactPath,
    DateTimeOffset CapturedAtUtc,
    int Width,
    int Height,
    AppInteractionSettling? Settling = null);
