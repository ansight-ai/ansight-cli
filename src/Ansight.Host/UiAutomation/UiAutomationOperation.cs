using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Operations.Tools.UiAutomation;

namespace Ansight.Host.UiAutomation;

public enum UiAutomationOperation
{
    Snapshot,
    Find,
    Tap,
    Type,
    Swipe,
    Pinch,
    Back,
    Wait,
    Assert,
    KeyboardOpen,
    KeyboardIsOpen,
    KeyboardDismiss,
    KeyboardPress,
    KeyboardSubmit,
    PlayPen,
    LongPress
}
