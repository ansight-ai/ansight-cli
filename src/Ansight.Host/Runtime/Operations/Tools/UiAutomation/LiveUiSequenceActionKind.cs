using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal enum LiveUiSequenceActionKind
{
    Tap,
    LongPress,
    Swipe,
    Pinch,
    Back
}
