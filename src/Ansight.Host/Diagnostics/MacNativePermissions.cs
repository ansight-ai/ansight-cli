using System.Reflection;
using System.Runtime.InteropServices;
using Ansight.Adb;
using SkiaSharp;

namespace Ansight.Host.Diagnostics;

internal static class MacNativePermissions
{
    [DllImport(
        "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics",
        EntryPoint = "CGPreflightScreenCaptureAccess")]
    private static extern byte PreflightScreenCaptureAccess();

    [DllImport(
        "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices",
        EntryPoint = "AXIsProcessTrusted")]
    private static extern byte IsProcessTrusted();

    public static bool HasScreenRecordingAccess() => PreflightScreenCaptureAccess() != 0;

    public static bool HasAccessibilityAccess() => IsProcessTrusted() != 0;
}
