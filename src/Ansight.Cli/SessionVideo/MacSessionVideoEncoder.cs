using System.Runtime.InteropServices;
using System.Text;
using Ansight.Host;

namespace Ansight.Cli.SessionVideo;

internal sealed class MacSessionVideoEncoder : ISessionVideoEncoder
{
    private const string NativeLibraryName = "libAnsightSessionVideoMac.dylib";
    private const int ErrorBufferCapacity = 4096;

    public string Name => "Apple AVFoundation/VideoToolbox H.264";

    public Task<SessionVideoEncodingResult> EncodeAsync(
        SessionVideoEncodingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("The Apple session video encoder requires macOS.");
        }
        if (request.Frames.Count == 0)
        {
            throw new ArgumentException("At least one frame is required.", nameof(request));
        }

        var outputDirectoryPath = Path.GetDirectoryName(request.OutputFilePath)
                                  ?? throw new ArgumentException(
                                      "The video output path must have a parent directory.",
                                      nameof(request));
        Directory.CreateDirectory(outputDirectoryPath);
        File.Delete(request.OutputFilePath);

        var errorBuffer = new StringBuilder(ErrorBufferCapacity);
        var encoder = NativeMethods.Create(
            request.OutputFilePath,
            request.Width,
            request.Height,
            request.AverageBitRate,
            errorBuffer,
            errorBuffer.Capacity);
        if (encoder == IntPtr.Zero)
        {
            throw CreateNativeException("AVFoundation could not create the H.264 encoder", errorBuffer);
        }

        try
        {
            var completedFrames = 0;
            foreach (var frame in request.Frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                errorBuffer.Clear();
                if (NativeMethods.AppendFrame(
                        encoder,
                        frame.SourceFilePath,
                        frame.PresentationTimeUs,
                        errorBuffer,
                        errorBuffer.Capacity) == 0)
                {
                    throw CreateNativeException(
                        $"AVFoundation rejected the frame at {frame.PresentationTimeUs}us",
                        errorBuffer);
                }
                request.ReportProgress?.Invoke(++completedFrames, request.Frames.Count);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var finalFrame = request.Frames[^1];
            var endTimeUs = checked(finalFrame.PresentationTimeUs + Math.Max(1, finalFrame.DurationUs));
            errorBuffer.Clear();
            if (NativeMethods.Finish(encoder, endTimeUs, errorBuffer, errorBuffer.Capacity) == 0)
            {
                throw CreateNativeException("AVFoundation could not finish the H.264 file", errorBuffer);
            }
        }
        finally
        {
            NativeMethods.Destroy(encoder);
        }

        return Task.FromResult(new SessionVideoEncodingResult(
            Name,
            request.Frames.Select(static frame => frame.PresentationTimeUs).ToArray()));
    }

    private static InvalidOperationException CreateNativeException(string message, StringBuilder errorBuffer)
        => errorBuffer.Length == 0
            ? new InvalidOperationException(message)
            : new InvalidOperationException($"{message}: {errorBuffer}");

    private static class NativeMethods
    {
        [DllImport(NativeLibraryName, EntryPoint = "AnsightSessionVideoMac_Create")]
        public static extern IntPtr Create(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string outputFilePath,
            int width,
            int height,
            int averageBitRate,
            [Out] StringBuilder errorBuffer,
            int errorBufferCapacity);

        [DllImport(NativeLibraryName, EntryPoint = "AnsightSessionVideoMac_AppendFrame")]
        public static extern int AppendFrame(
            IntPtr encoder,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string sourceFilePath,
            long presentationTimeUs,
            [Out] StringBuilder errorBuffer,
            int errorBufferCapacity);

        [DllImport(NativeLibraryName, EntryPoint = "AnsightSessionVideoMac_Finish")]
        public static extern int Finish(
            IntPtr encoder,
            long endTimeUs,
            [Out] StringBuilder errorBuffer,
            int errorBufferCapacity);

        [DllImport(NativeLibraryName, EntryPoint = "AnsightSessionVideoMac_Destroy")]
        public static extern void Destroy(IntPtr encoder);
    }
}
