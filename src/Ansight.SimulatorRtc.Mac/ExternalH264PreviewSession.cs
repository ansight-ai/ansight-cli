using System.Text;

namespace Ansight.SimulatorRtc.Mac;

public sealed class ExternalH264PreviewSession : IDisposable
{
    private const int ErrorBufferCapacity = 2048;
    private IntPtr nativeHandle;
    private bool disposed;

    public ExternalH264PreviewSession()
    {
        var errorBuffer = new byte[ErrorBufferCapacity];
        nativeHandle = NativeMethods.ExternalH264PreviewViewCreate(
            errorBuffer,
            (nuint)errorBuffer.Length);
        if (nativeHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException(ReadBuffer(errorBuffer, "The native Android H.264 preview could not be created."));
        }

        NativeLayerHandle = NativeMethods.ExternalH264PreviewViewGetLayer(nativeHandle);
        if (NativeLayerHandle == IntPtr.Zero)
        {
            Dispose();
            throw new InvalidOperationException("The native Android H.264 display layer could not be obtained.");
        }
    }

    public IntPtr NativeLayerHandle { get; }

    public void EnqueueAccessUnit(
        byte[] accessUnit,
        long presentationTimestampMicroseconds,
        bool isKeyFrame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(accessUnit);
        if (accessUnit.Length == 0)
        {
            throw new ArgumentException("An H.264 access unit is required.", nameof(accessUnit));
        }

        var errorBuffer = new byte[ErrorBufferCapacity];
        if (!NativeMethods.ExternalH264PreviewViewEnqueueAccessUnit(
                nativeHandle,
                accessUnit,
                accessUnit.Length,
                presentationTimestampMicroseconds,
                isKeyFrame,
                errorBuffer,
                (nuint)errorBuffer.Length))
        {
            throw new InvalidOperationException(ReadBuffer(errorBuffer, "The native Android decoder rejected an H.264 frame."));
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        if (nativeHandle != IntPtr.Zero)
        {
            NativeMethods.ExternalH264PreviewViewDestroy(nativeHandle);
            nativeHandle = IntPtr.Zero;
        }
    }

    private static string ReadBuffer(byte[] buffer, string fallback)
    {
        var terminator = Array.IndexOf(buffer, (byte)0);
        var length = terminator < 0 ? buffer.Length : terminator;
        var value = Encoding.UTF8.GetString(buffer, 0, length).Trim();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
