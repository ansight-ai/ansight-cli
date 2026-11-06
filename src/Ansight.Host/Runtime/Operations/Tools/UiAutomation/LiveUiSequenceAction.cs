using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed record LiveUiSequenceAction(
    LiveUiSequenceActionKind Kind,
    LiveUiScreenPoint? TapPoint,
    LiveUiSwipePath? SwipePath,
    LiveUiPinchPath? PinchPath,
    int PauseAfterMilliseconds,
    int? HoldDurationMilliseconds = null);
