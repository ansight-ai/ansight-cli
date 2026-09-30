using System.Runtime.InteropServices;
using Ansight.MacSimulatorHid;

namespace Ansight.MacSimulatorHid.Interop;

internal static class NativeMethods
{
    private const string Library = "AnsightSimulatorHid";

    [DllImport(Library, EntryPoint = "AnsightSimulatorHidCheckCompatibility")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool CheckCompatibility(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string developerDirectory,
        byte[] errorBuffer,
        nuint errorBufferCapacity);

    [DllImport(Library, EntryPoint = "AnsightSimulatorHidSessionCreate")]
    internal static extern SessionHandle SessionCreate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string developerDirectory,
        byte[] errorBuffer,
        nuint errorBufferCapacity);

    [DllImport(Library, EntryPoint = "AnsightSimulatorHidSessionSendPointer")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SessionSendPointer(
        SessionHandle session,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string deviceUdid,
        MacSimulatorPointerPhase phase,
        double normalizedX,
        double normalizedY,
        [MarshalAs(UnmanagedType.I1)] bool hasSecondaryContact,
        double secondaryNormalizedX,
        double secondaryNormalizedY,
        long pointerId,
        long timestampMilliseconds,
        byte[] errorBuffer,
        nuint errorBufferCapacity);

    [DllImport(Library, EntryPoint = "AnsightSimulatorHidSessionGetMainScreenMetrics")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SessionGetMainScreenMetrics(
        SessionHandle session,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string deviceUdid,
        out double pixelWidth,
        out double pixelHeight,
        out double scale,
        byte[] errorBuffer,
        nuint errorBufferCapacity);

    [DllImport(Library, EntryPoint = "AnsightSimulatorHidSessionCopyAccessibilityTreeJson")]
    internal static extern IntPtr SessionCopyAccessibilityTreeJson(
        SessionHandle session,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string deviceUdid,
        byte[] errorBuffer,
        nuint errorBufferCapacity);

    [DllImport(Library, EntryPoint = "AnsightSimulatorHidFreeString")]
    internal static extern void FreeString(IntPtr value);

    [DllImport(Library, EntryPoint = "AnsightSimulatorHidSessionSendButton")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SessionSendButton(
        SessionHandle session,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string deviceUdid,
        MacSimulatorButton button,
        MacSimulatorButtonPhase phase,
        byte[] errorBuffer,
        nuint errorBufferCapacity);

    [DllImport(Library, EntryPoint = "AnsightSimulatorHidSessionSendKey")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SessionSendKey(
        SessionHandle session,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string deviceUdid,
        uint usageCode,
        MacSimulatorKeyPhase phase,
        byte[] errorBuffer,
        nuint errorBufferCapacity);

    [DllImport(Library, EntryPoint = "AnsightSimulatorHidSessionDestroy")]
    internal static extern void SessionDestroy(IntPtr session);
}
