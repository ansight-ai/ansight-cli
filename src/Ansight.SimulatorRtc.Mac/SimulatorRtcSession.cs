using System.Runtime.InteropServices;
using System.Text;

namespace Ansight.SimulatorRtc.Mac;

public sealed class SimulatorRtcSession : IDisposable
{
    private const int ErrorBufferSize = 4096;
    private const int AnswerBufferSize = 256 * 1024;
    private static readonly NativeMethods.InputCallback InputCallback = HandleNativeInput;
    private readonly GCHandle callbackHandle;
    private IntPtr handle;
    private bool disposed;

    public SimulatorRtcSession(
        string developerDirectory,
        string deviceUdid,
        int framesPerSecond = 30,
        int averageBitRate = 5_000_000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(developerDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceUdid);
        if (framesPerSecond is < 1 or > 60)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        }

        if (averageBitRate is < 250_000 or > 50_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(averageBitRate));
        }

        DeviceUdid = deviceUdid;
        callbackHandle = GCHandle.Alloc(this);
        var errorBuffer = new byte[ErrorBufferSize];
        handle = NativeMethods.SessionCreate(
            developerDirectory,
            deviceUdid,
            framesPerSecond,
            averageBitRate,
            InputCallback,
            GCHandle.ToIntPtr(callbackHandle),
            errorBuffer,
            (nuint)errorBuffer.Length);
        if (handle == IntPtr.Zero)
        {
            callbackHandle.Free();
            throw new SimulatorRtcException(ReadBuffer(errorBuffer, "Could not create the Simulator WebRTC session."));
        }
    }

    public event EventHandler<SimulatorRtcInputEventArgs>? InputReceived;

    public string DeviceUdid { get; }

    public async Task<SimulatorRtcDescription> CreateAnswerAsync(
        SimulatorRtcDescription offer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ApplyOffer(offer);
        var answerBuffer = new byte[AnswerBufferSize];
        var errorBuffer = new byte[ErrorBufferSize];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = NativeMethods.SessionCopyAnswer(
                handle,
                answerBuffer,
                (nuint)answerBuffer.Length,
                errorBuffer,
                (nuint)errorBuffer.Length);
            if (result > 0)
            {
                return new SimulatorRtcDescription("answer", ReadBuffer(answerBuffer, ""));
            }

            if (result < 0)
            {
                throw new SimulatorRtcException(ReadBuffer(errorBuffer, "Simulator WebRTC answer creation failed."));
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ApplyOffer(SimulatorRtcDescription offer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(offer);
        if (!string.Equals(offer.Type, "offer", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The remote description must be a WebRTC offer.", nameof(offer));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(offer.Sdp);
        var errorBuffer = new byte[ErrorBufferSize];
        if (!NativeMethods.SessionSetOffer(
                handle,
                offer.Sdp,
                errorBuffer,
                (nuint)errorBuffer.Length))
        {
            throw new SimulatorRtcException(ReadBuffer(errorBuffer, "Could not apply the WebRTC offer."));
        }
    }

    public bool TrySendMessage(string message)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var data = Encoding.UTF8.GetBytes(message);
        var errorBuffer = new byte[ErrorBufferSize];
        var result = NativeMethods.SessionSendMessage(
            handle,
            data,
            data.Length,
            errorBuffer,
            (nuint)errorBuffer.Length);
        if (result < 0)
        {
            throw new SimulatorRtcException(ReadBuffer(errorBuffer, "Could not send a WebRTC data-channel message."));
        }
        return result > 0;
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
            NativeMethods.SessionDestroy(activeHandle);
        }

        if (callbackHandle.IsAllocated)
        {
            callbackHandle.Free();
        }
    }

    private static void HandleNativeInput(IntPtr message, int size, IntPtr context)
    {
        if (message == IntPtr.Zero || size <= 0 || context == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var session = GCHandle.FromIntPtr(context).Target as SimulatorRtcSession;
            if (session is null || session.disposed)
            {
                return;
            }

            var payload = Marshal.PtrToStringUTF8(message, size);
            if (!string.IsNullOrWhiteSpace(payload))
            {
                session.InputReceived?.Invoke(session, new SimulatorRtcInputEventArgs(payload));
            }
        }
        catch
        {
            // Exceptions must never cross the reverse P/Invoke boundary.
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
