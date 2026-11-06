using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal enum LiveUiActionKind
{
    Tap,
    LongPress,
    TypeText,
    Swipe,
    Scroll,
    Pinch,
    Back,
    KeyboardOpen,
    KeyboardDismiss,
    KeyboardPress,
    KeyboardSubmit
}
