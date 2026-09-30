using Microsoft.Win32.SafeHandles;

namespace Ansight.MacSimulatorHid.Interop;

internal sealed class SessionHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SessionHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.SessionDestroy(handle);
        return true;
    }
}
