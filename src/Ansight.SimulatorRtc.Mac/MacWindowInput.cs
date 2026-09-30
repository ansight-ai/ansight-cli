using System.Text;

namespace Ansight.SimulatorRtc.Mac;

public static class MacWindowInput
{
    private const int ErrorBufferSize = 4096;

    public static bool IsAccessibilityTrusted(bool prompt = false)
        => NativeMethods.MacWindowInputIsTrusted(prompt);

    public static bool IsScreenRecordingAuthorized()
        => NativeMethods.MacWindowScreenCaptureIsAuthorized();

    public static void SendPointer(
        int processIdentifier,
        MacWindowPointerPhase phase,
        MacWindowPointerButton button,
        double normalizedX,
        double normalizedY,
        double scrollDeltaX = 0,
        double scrollDeltaY = 0)
    {
        ValidateProcessIdentifier(processIdentifier);
        var errorBuffer = new byte[ErrorBufferSize];
        if (!NativeMethods.MacWindowInputSendPointer(
                processIdentifier,
                (int)phase,
                (int)button,
                normalizedX,
                normalizedY,
                scrollDeltaX,
                scrollDeltaY,
                errorBuffer,
                (nuint)errorBuffer.Length))
        {
            throw new SimulatorRtcException(ReadBuffer(errorBuffer, "Could not forward the macOS pointer event."));
        }
    }

    public static void SendKey(int processIdentifier, uint usageCode, bool isKeyDown)
    {
        ValidateProcessIdentifier(processIdentifier);
        var errorBuffer = new byte[ErrorBufferSize];
        if (!NativeMethods.MacWindowInputSendKey(
                processIdentifier,
                usageCode,
                isKeyDown,
                errorBuffer,
                (nuint)errorBuffer.Length))
        {
            throw new SimulatorRtcException(ReadBuffer(errorBuffer, "Could not forward the macOS keyboard event."));
        }
    }

    public static void SendText(int processIdentifier, string text)
    {
        ValidateProcessIdentifier(processIdentifier);
        ArgumentNullException.ThrowIfNull(text);
        var errorBuffer = new byte[ErrorBufferSize];
        if (!NativeMethods.MacWindowInputSendText(
                processIdentifier,
                text,
                errorBuffer,
                (nuint)errorBuffer.Length))
        {
            throw new SimulatorRtcException(ReadBuffer(errorBuffer, "Could not forward macOS text input."));
        }
    }

    private static void ValidateProcessIdentifier(int processIdentifier)
    {
        if (processIdentifier <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processIdentifier));
        }
    }

    private static string ReadBuffer(byte[] buffer, string fallback)
    {
        var length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
        {
            length = buffer.Length;
        }

        var message = Encoding.UTF8.GetString(buffer, 0, length).Trim();
        return string.IsNullOrWhiteSpace(message) ? fallback : message;
    }
}
