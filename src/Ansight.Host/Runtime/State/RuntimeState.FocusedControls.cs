namespace Ansight.Host.Runtime.State;

using Ansight.Host;

internal sealed partial class RuntimeState
{
    private readonly FocusedControlStore focusedControls = new();

    public IReadOnlyList<RuntimeFocusedControlSnapshot> GetFocusedControlSnapshots()
        => focusedControls.GetSnapshots();

    public void SetFocusedControl(RuntimeFocusedControlSnapshot snapshot)
        => focusedControls.Set(snapshot);

    public void ClearFocusedControl(string? sessionId = null)
        => focusedControls.Clear(sessionId);
}
