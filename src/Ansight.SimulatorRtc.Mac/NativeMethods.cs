using System.Runtime.InteropServices;

namespace Ansight.SimulatorRtc.Mac;

internal static class NativeMethods
{
    private const string InternalLibrary = "libAnsightSimulatorRtc";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void InputCallback(IntPtr message, int size, IntPtr context);

    [DllImport(InternalLibrary, EntryPoint = "AnsightSimulatorRtcSessionCreate")]
    internal static extern IntPtr SessionCreate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string developerDirectory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string deviceUdid,
        int framesPerSecond,
        int averageBitRate,
        InputCallback inputCallback,
        IntPtr inputContext,
        byte[] errorBuffer,
        nuint errorBufferCapacity);

    [DllImport(InternalLibrary, EntryPoint = "AnsightExternalH264RtcSessionCreate")]
    internal static extern IntPtr ExternalH264SessionCreate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string deviceIdentifier,
        int framesPerSecond,
        InputCallback inputCallback,
        IntPtr inputContext,
        byte[] errorBuffer,
        nuint errorBufferCapacity);

    [DllImport(InternalLibrary, EntryPoint = "AnsightMacWindowRtcSessionCreate")]
    internal static extern IntPtr MacWindowSessionCreate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string deviceIdentifier,
        int processIdentifier,
        int framesPerSecond,
        int averageBitRate,
        InputCallback inputCallback,
        IntPtr inputContext,
        byte[] errorBuffer,
        nuint errorBufferCapacity);

    [DllImport(InternalLibrary, EntryPoint = "AnsightMacWindowRtcIsSupported")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool MacWindowRtcIsSupported();

    [DllImport(InternalLibrary, EntryPoint = "AnsightMacWindowPreviewViewCreate")]
    internal static extern IntPtr MacWindowPreviewViewCreate(
        int processIdentifier,
        int framesPerSecond,
        byte[] errorBuffer,
        nuint errorBufferCapacity);

    [DllImport(InternalLibrary, EntryPoint = "AnsightMacWindowPreviewViewGetLayer")]
    internal static extern IntPtr MacWindowPreviewViewGetLayer(IntPtr previewView);

    [DllImport(InternalLibrary, EntryPoint = "AnsightMacWindowPreviewViewGetSurfaceSize")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool MacWindowPreviewViewGetSurfaceSize(
        IntPtr previewView,
        out int width,
        out int height);

    [DllImport(InternalLibrary, EntryPoint = "AnsightMacWindowPreviewViewCopyLastError")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool MacWindowPreviewViewCopyLastError(
        IntPtr previewView,
        byte[] errorBuffer,
        nuint errorBufferCapacity);

    [DllImport(InternalLibrary, EntryPoint = "AnsightMacWindowPreviewViewDestroy")]
    internal static extern void MacWindowPreviewViewDestroy(IntPtr previewView);

    [DllImport(InternalLibrary, EntryPoint = "AnsightMacWindowInputIsTrusted")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool MacWindowInputIsTrusted([MarshalAs(UnmanagedType.I1)] bool prompt);

    [DllImport(InternalLibrary, EntryPoint = "AnsightMacWindowScreenCaptureIsAuthorized")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool MacWindowScreenCaptureIsAuthorized();

    [DllImport(InternalLibrary, EntryPoint = "AnsightMacWindowInputSendPointer")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool MacWindowInputSendPointer(
        int processIdentifier,
        int phase,
        int button,
        double normalizedX,
        double normalizedY,
        double scrollDeltaX,
        double scrollDeltaY,
        byte[] errorBuffer,
        nuint errorBufferCapacity);

    [DllImport(InternalLibrary, EntryPoint = "AnsightMacWindowInputSendKey")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool MacWindowInputSendKey(
        int processIdentifier,
        uint usageCode,
        [MarshalAs(UnmanagedType.I1)] bool isKeyDown,
        byte[] errorBuffer,
        nuint errorBufferCapacity);

    [DllImport(InternalLibrary, EntryPoint = "AnsightMacWindowInputSendText")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool MacWindowInputSendText(
        int processIdentifier,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string text,
        byte[] errorBuffer,
        nuint errorBufferCapacity);

    [DllImport(InternalLibrary, EntryPoint = "AnsightExternalH264RtcSessionSendAccessUnit")]
    internal static extern int ExternalH264SessionSendAccessUnit(
        IntPtr session,
        byte[] data,
        int size,
        long presentationTimestampMicroseconds,
        [MarshalAs(UnmanagedType.I1)] bool isKeyFrame,
        byte[] errorBuffer,
        nuint errorBufferCapacity);

    [DllImport(InternalLibrary, EntryPoint = "AnsightSimulatorRtcSessionCopyAnswer")]
    internal static extern int SessionCopyAnswer(
        IntPtr session,
        byte[] answerBuffer,
        nuint answerBufferCapacity,
        byte[] errorBuffer,
        nuint errorBufferCapacity);

    [DllImport(InternalLibrary, EntryPoint = "AnsightSimulatorRtcSessionSetOffer")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SessionSetOffer(
        IntPtr session,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string offerSdp,
        byte[] errorBuffer,
        nuint errorBufferCapacity);

    [DllImport(InternalLibrary, EntryPoint = "AnsightSimulatorRtcSessionSendMessage")]
    internal static extern int SessionSendMessage(
        IntPtr session,
        byte[] data,
        int size,
        byte[] errorBuffer,
        nuint errorBufferCapacity);

    [DllImport(InternalLibrary, EntryPoint = "AnsightSimulatorRtcSessionDestroy")]
    internal static extern void SessionDestroy(IntPtr session);

    [DllImport(InternalLibrary, EntryPoint = "AnsightSimulatorPreviewViewCreate")]
    internal static extern IntPtr PreviewViewCreate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string developerDirectory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string deviceUdid,
        byte[] errorBuffer,
        nuint errorBufferCapacity);

    [DllImport(InternalLibrary, EntryPoint = "AnsightSimulatorPreviewViewGetLayer")]
    internal static extern IntPtr PreviewViewGetLayer(IntPtr previewView);

    [DllImport(InternalLibrary, EntryPoint = "AnsightSimulatorPreviewViewGetSurfaceSize")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool PreviewViewGetSurfaceSize(
        IntPtr previewView,
        out int width,
        out int height);

    [DllImport(InternalLibrary, EntryPoint = "AnsightSimulatorPreviewViewRenderCurrentFrame")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool PreviewViewRenderCurrentFrame(IntPtr previewView);

    [DllImport(InternalLibrary, EntryPoint = "AnsightSimulatorPreviewViewSetFramesPerSecond")]
    internal static extern void PreviewViewSetFramesPerSecond(
        IntPtr previewView,
        int framesPerSecond);

    [DllImport(InternalLibrary, EntryPoint = "AnsightSimulatorPreviewViewSetDrawableSize")]
    internal static extern void PreviewViewSetDrawableSize(
        IntPtr previewView,
        double width,
        double height);

    [DllImport(InternalLibrary, EntryPoint = "AnsightSimulatorPreviewViewGetIsDeviceLocked")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool PreviewViewGetIsDeviceLocked(
        IntPtr previewView,
        [MarshalAs(UnmanagedType.I1)] out bool isDeviceLocked);

    [DllImport(InternalLibrary, EntryPoint = "AnsightSimulatorPreviewViewDestroy")]
    internal static extern void PreviewViewDestroy(IntPtr previewView);

    [DllImport(InternalLibrary, EntryPoint = "AnsightExternalH264PreviewViewCreate")]
    internal static extern IntPtr ExternalH264PreviewViewCreate(
        byte[] errorBuffer,
        nuint errorBufferCapacity);

    [DllImport(InternalLibrary, EntryPoint = "AnsightExternalH264PreviewViewGetLayer")]
    internal static extern IntPtr ExternalH264PreviewViewGetLayer(IntPtr previewView);

    [DllImport(InternalLibrary, EntryPoint = "AnsightExternalH264PreviewViewEnqueueAccessUnit")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool ExternalH264PreviewViewEnqueueAccessUnit(
        IntPtr previewView,
        byte[] data,
        int size,
        long presentationTimestampMicroseconds,
        [MarshalAs(UnmanagedType.I1)] bool isKeyFrame,
        byte[] errorBuffer,
        nuint errorBufferCapacity);

    [DllImport(InternalLibrary, EntryPoint = "AnsightExternalH264PreviewViewDestroy")]
    internal static extern void ExternalH264PreviewViewDestroy(IntPtr previewView);
}
