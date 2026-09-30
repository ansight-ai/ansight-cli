using System.Text;

namespace Ansight.SimulatorRtc.Mac;

public sealed class MacWindowPreviewSession : IDisposable
{
    private const int ErrorBufferSize = 4096;
    private IntPtr handle;
    private bool disposed;

    public MacWindowPreviewSession(int processIdentifier, int framesPerSecond)
    {
        if (processIdentifier <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processIdentifier));
        }
        if (framesPerSecond is not (30 or 60))
        {
            throw new ArgumentOutOfRangeException(
                nameof(framesPerSecond),
                "The macOS window preview frame rate must be 30 or 60 FPS.");
        }

        var errorBuffer = new byte[ErrorBufferSize];
        handle = NativeMethods.MacWindowPreviewViewCreate(
            processIdentifier,
            framesPerSecond,
            errorBuffer,
            (nuint)errorBuffer.Length);
        if (handle == IntPtr.Zero)
        {
            throw new SimulatorRtcException(
                ReadBuffer(errorBuffer, "Could not create the macOS window preview."));
        }

        NativeLayerHandle = NativeMethods.MacWindowPreviewViewGetLayer(handle);
        if (NativeLayerHandle == IntPtr.Zero)
        {
            Dispose();
            throw new SimulatorRtcException("The native macOS window preview layer could not be created.");
        }
        var hasInitialError = TryGetLastError(out var initialError);
        if (hasInitialError)
        {
            Dispose();
            throw new SimulatorRtcException(initialError);
        }
    }

    public IntPtr NativeLayerHandle { get; }

    public bool TryGetSurfaceSize(out SimulatorPreviewSurfaceSize surfaceSize)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (NativeMethods.MacWindowPreviewViewGetSurfaceSize(handle, out var width, out var height))
        {
            surfaceSize = new SimulatorPreviewSurfaceSize(width, height);
            return true;
        }

        surfaceSize = default;
        return false;
    }

    public bool TryGetLastError(out string message)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var errorBuffer = new byte[ErrorBufferSize];
        if (NativeMethods.MacWindowPreviewViewCopyLastError(
                handle,
                errorBuffer,
                (nuint)errorBuffer.Length))
        {
            message = ReadBuffer(errorBuffer, "macOS window capture failed.");
            return true;
        }

        message = string.Empty;
        return false;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        var activeHandle = Interlocked.Exchange(ref handle, IntPtr.Zero);
        if (activeHandle != IntPtr.Zero)
        {
            NativeMethods.MacWindowPreviewViewDestroy(activeHandle);
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
