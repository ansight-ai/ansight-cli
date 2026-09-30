using System.Text;

namespace Ansight.SimulatorRtc.Mac;

public sealed class SimulatorPreviewSession : IDisposable
{
    private const int ErrorBufferSize = 4096;
    private IntPtr handle;
    private bool disposed;

    public SimulatorPreviewSession(
        string developerDirectory,
        string deviceUdid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(developerDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceUdid);

        var errorBuffer = new byte[ErrorBufferSize];
        handle = NativeMethods.PreviewViewCreate(
            developerDirectory,
            deviceUdid,
            errorBuffer,
            (nuint)errorBuffer.Length);
        if (handle == IntPtr.Zero)
        {
            throw new SimulatorRtcException(
                ReadBuffer(errorBuffer, "Could not create the native Simulator preview."));
        }
    }

    public IntPtr NativeLayerHandle
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return NativeMethods.PreviewViewGetLayer(handle);
        }
    }

    public bool TryGetSurfaceSize(out SimulatorPreviewSurfaceSize surfaceSize)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (NativeMethods.PreviewViewGetSurfaceSize(handle, out var width, out var height))
        {
            surfaceSize = new SimulatorPreviewSurfaceSize(width, height);
            return true;
        }

        surfaceSize = default;
        return false;
    }

    public bool TryRenderCurrentFrame()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return NativeMethods.PreviewViewRenderCurrentFrame(handle);
    }

    public void SetFramesPerSecond(int framesPerSecond)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (framesPerSecond is not (30 or 60))
        {
            throw new ArgumentOutOfRangeException(
                nameof(framesPerSecond),
                "Simulator preview frame rate must be 30 or 60 FPS.");
        }

        NativeMethods.PreviewViewSetFramesPerSecond(handle, framesPerSecond);
    }

    public void SetDrawableSize(double width, double height)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        NativeMethods.PreviewViewSetDrawableSize(
            handle,
            Math.Max(0, width),
            Math.Max(0, height));
    }

    public bool TryGetIsDeviceLocked(out bool isDeviceLocked)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return NativeMethods.PreviewViewGetIsDeviceLocked(handle, out isDeviceLocked);
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
            NativeMethods.PreviewViewDestroy(activeHandle);
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
