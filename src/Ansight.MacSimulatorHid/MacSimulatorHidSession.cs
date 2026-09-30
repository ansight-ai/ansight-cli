using System.Runtime.InteropServices;
using System.Text;
using Ansight.MacSimulatorHid.Interop;

namespace Ansight.MacSimulatorHid;

public sealed class MacSimulatorHidSession : IDisposable
{
    private const int ErrorBufferSize = 2048;
    private readonly SessionHandle handle;
    private bool disposed;

    public MacSimulatorHidSession(string developerDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(developerDirectory);
        var errorBuffer = new byte[ErrorBufferSize];
        handle = NativeMethods.SessionCreate(
            developerDirectory,
            errorBuffer,
            (nuint)errorBuffer.Length);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new MacSimulatorHidException(ReadError(
                errorBuffer,
                "Could not create the Simulator HID session."));
        }
    }

    public void SendPointer(
        string deviceUdid,
        MacSimulatorPointerPhase phase,
        double normalizedX,
        double normalizedY,
        long pointerId,
        long timestampMilliseconds)
        => SendPointer(
            deviceUdid,
            phase,
            normalizedX,
            normalizedY,
            secondaryContact: null,
            pointerId,
            timestampMilliseconds);

    public void SendPointer(
        string deviceUdid,
        MacSimulatorPointerPhase phase,
        double normalizedX,
        double normalizedY,
        MacSimulatorTouchContact? secondaryContact,
        long pointerId,
        long timestampMilliseconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceUdid);
        if (!double.IsFinite(normalizedX) || !double.IsFinite(normalizedY))
        {
            throw new ArgumentException("Pointer coordinates must be finite.");
        }

        var errorBuffer = new byte[ErrorBufferSize];
        var success = NativeMethods.SessionSendPointer(
            handle,
            deviceUdid,
            phase,
            Math.Clamp(normalizedX, 0, 1),
            Math.Clamp(normalizedY, 0, 1),
            secondaryContact.HasValue,
            secondaryContact.HasValue ? Math.Clamp(secondaryContact.Value.NormalizedX, 0, 1) : 0,
            secondaryContact.HasValue ? Math.Clamp(secondaryContact.Value.NormalizedY, 0, 1) : 0,
            pointerId,
            timestampMilliseconds,
            errorBuffer,
            (nuint)errorBuffer.Length);
        if (!success)
        {
            throw new MacSimulatorHidException(ReadError(
                errorBuffer,
                "Simulator HID input failed."));
        }
    }

    public void SendButton(
        string deviceUdid,
        MacSimulatorButton button,
        MacSimulatorButtonPhase phase)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceUdid);
        var errorBuffer = new byte[ErrorBufferSize];
        var success = NativeMethods.SessionSendButton(
            handle,
            deviceUdid,
            button,
            phase,
            errorBuffer,
            (nuint)errorBuffer.Length);
        if (!success)
        {
            throw new MacSimulatorHidException(ReadError(
                errorBuffer,
                "Simulator button input failed."));
        }
    }

    public MacSimulatorDisplayMetrics GetMainScreenMetrics(string deviceUdid)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceUdid);
        var errorBuffer = new byte[ErrorBufferSize];
        var success = NativeMethods.SessionGetMainScreenMetrics(
            handle,
            deviceUdid,
            out var pixelWidth,
            out var pixelHeight,
            out var scale,
            errorBuffer,
            (nuint)errorBuffer.Length);
        if (!success)
        {
            throw new MacSimulatorHidException(ReadError(
                errorBuffer,
                "Could not read the Simulator main-screen metrics."));
        }

        return new MacSimulatorDisplayMetrics(pixelWidth, pixelHeight, scale);
    }

    public string CaptureAccessibilityTreeJson(string deviceUdid)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceUdid);
        var errorBuffer = new byte[ErrorBufferSize];
        var nativeJson = NativeMethods.SessionCopyAccessibilityTreeJson(
            handle,
            deviceUdid,
            errorBuffer,
            (nuint)errorBuffer.Length);
        if (nativeJson == IntPtr.Zero)
        {
            throw new MacSimulatorHidException(ReadError(
                errorBuffer,
                "Could not read the Simulator accessibility hierarchy."));
        }

        try
        {
            return Marshal.PtrToStringUTF8(nativeJson)
                   ?? throw new MacSimulatorHidException(
                       "The Simulator accessibility hierarchy was not valid UTF-8.");
        }
        finally
        {
            NativeMethods.FreeString(nativeJson);
        }
    }

    public void SendKey(
        string deviceUdid,
        uint usageCode,
        MacSimulatorKeyPhase phase)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceUdid);
        if (usageCode is < 4 or > 231)
        {
            throw new ArgumentOutOfRangeException(
                nameof(usageCode),
                "A USB HID keyboard usage code between 4 and 231 is required.");
        }

        var errorBuffer = new byte[ErrorBufferSize];
        var success = NativeMethods.SessionSendKey(
            handle,
            deviceUdid,
            usageCode,
            phase,
            errorBuffer,
            (nuint)errorBuffer.Length);
        if (!success)
        {
            throw new MacSimulatorHidException(ReadError(
                errorBuffer,
                "Simulator keyboard input failed."));
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        handle.Dispose();
    }

    private static string ReadError(byte[] buffer, string fallback)
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
