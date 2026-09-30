#if WINDOWS
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Ansight.Host;
using SkiaSharp;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Ansight.Cli.SessionVideo;

internal sealed class WindowsSessionVideoEncoder : ISessionVideoEncoder
{
    public string Name => "Windows Media Foundation H.264";

    public async Task<SessionVideoEncodingResult> EncodeAsync(
        SessionVideoEncodingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Frames.Count == 0)
        {
            throw new ArgumentException("At least one frame is required.", nameof(request));
        }

        var outputDirectoryPath = Path.GetDirectoryName(request.OutputFilePath)
                                  ?? throw new ArgumentException(
                                      "The video output path must have a parent directory.",
                                      nameof(request));
        Directory.CreateDirectory(outputDirectoryPath);
        var outputFolder = await StorageFolder.GetFolderFromPathAsync(outputDirectoryPath)
            .AsTask(cancellationToken).ConfigureAwait(false);
        var outputFile = await outputFolder.CreateFileAsync(
                Path.GetFileName(request.OutputFilePath),
                CreationCollisionOption.ReplaceExisting)
            .AsTask(cancellationToken).ConfigureAwait(false);

        var sourceProperties = VideoEncodingProperties.CreateUncompressed(
            MediaEncodingSubtypes.Bgra8,
            checked((uint)request.Width),
            checked((uint)request.Height));
        sourceProperties.FrameRate.Numerator = 30;
        sourceProperties.FrameRate.Denominator = 1;
        sourceProperties.PixelAspectRatio.Numerator = 1;
        sourceProperties.PixelAspectRatio.Denominator = 1;
        var streamDescriptor = new VideoStreamDescriptor(sourceProperties);
        var streamSource = new MediaStreamSource(streamDescriptor)
        {
            BufferTime = TimeSpan.Zero,
            CanSeek = false,
            Duration = TimeSpan.FromTicks(checked(
                (request.Frames[^1].PresentationTimeUs + Math.Max(1, request.Frames[^1].DurationUs)) * 10))
        };
        var frameProvider = new WindowsSessionVideoFrameProvider(request);
        streamSource.SampleRequested += frameProvider.HandleSampleRequested;

        try
        {
            var targetProfile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
            targetProfile.Video.Width = checked((uint)request.Width);
            targetProfile.Video.Height = checked((uint)request.Height);
            targetProfile.Video.Bitrate = checked((uint)request.AverageBitRate);
            targetProfile.Video.FrameRate.Numerator = 30;
            targetProfile.Video.FrameRate.Denominator = 1;
            targetProfile.Video.PixelAspectRatio.Numerator = 1;
            targetProfile.Video.PixelAspectRatio.Denominator = 1;

            var transcoder = new MediaTranscoder
            {
                AlwaysReencode = true,
                HardwareAccelerationEnabled = true
            };
            using var outputStream = await outputFile.OpenAsync(FileAccessMode.ReadWrite)
                .AsTask(cancellationToken).ConfigureAwait(false);
            var preparation = await transcoder.PrepareMediaStreamSourceTranscodeAsync(
                    streamSource,
                    outputStream,
                    targetProfile)
                .AsTask(cancellationToken).ConfigureAwait(false);
            if (!preparation.CanTranscode)
            {
                throw new InvalidOperationException(
                    $"Media Foundation could not prepare the H.264 encoder: {preparation.FailureReason}.");
            }

            await preparation.TranscodeAsync().AsTask(cancellationToken).ConfigureAwait(false);
            frameProvider.ThrowIfFailed();
            return new SessionVideoEncodingResult(Name, frameProvider.WrittenPresentationTimesUs);
        }
        finally
        {
            streamSource.SampleRequested -= frameProvider.HandleSampleRequested;
        }
    }

    private sealed class WindowsSessionVideoFrameProvider
    {
        private readonly SessionVideoEncodingRequest request;
        private readonly object sampleSync = new();
        private readonly List<long> writtenPresentationTimesUs;
        private int nextFrameIndex;
        private Exception? failure;

        public WindowsSessionVideoFrameProvider(SessionVideoEncodingRequest request)
        {
            this.request = request;
            writtenPresentationTimesUs = new List<long>(request.Frames.Count);
        }

        public IReadOnlyList<long> WrittenPresentationTimesUs => writtenPresentationTimesUs;

        public void HandleSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            lock (sampleSync)
            {
                try
                {
                    if (failure is not null || nextFrameIndex >= request.Frames.Count)
                    {
                        args.Request.Sample = null;
                        return;
                    }

                    var frame = request.Frames[nextFrameIndex++];
                    var bytes = RenderFrame(frame.SourceFilePath, request.Width, request.Height);
                    IBuffer buffer = bytes.AsBuffer();
                    var sample = MediaStreamSample.CreateFromBuffer(
                        buffer,
                        TimeSpan.FromTicks(checked(frame.PresentationTimeUs * 10)));
                    sample.Duration = TimeSpan.FromTicks(checked(Math.Max(1, frame.DurationUs) * 10));
                    sample.KeyFrame = true;
                    args.Request.Sample = sample;
                    writtenPresentationTimesUs.Add(frame.PresentationTimeUs);
                    request.ReportProgress?.Invoke(nextFrameIndex, request.Frames.Count);
                }
                catch (Exception exception)
                {
                    failure = exception;
                    args.Request.Sample = null;
                }
            }
        }

        public void ThrowIfFailed()
        {
            if (failure is not null)
            {
                throw new InvalidOperationException("Media Foundation could not supply a screenshot frame.", failure);
            }
        }

        private static byte[] RenderFrame(string sourceFilePath, int width, int height)
        {
            using var sourceBitmap = SKBitmap.Decode(sourceFilePath)
                                     ?? throw new InvalidDataException(
                                         $"Unable to decode screenshot '{sourceFilePath}'.");
            var imageInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var outputBitmap = new SKBitmap(imageInfo);
            using (var canvas = new SKCanvas(outputBitmap))
            using (var paint = new SKPaint { IsAntialias = true })
            {
                canvas.Clear(SKColors.Black);
                canvas.DrawBitmap(sourceBitmap, new SKRect(0, 0, width, height), paint);
                canvas.Flush();
            }

            var copiedBytesPerRow = checked(width * 4);
            var bytes = new byte[checked(copiedBytesPerRow * height)];
            var sourceAddress = outputBitmap.GetPixels();
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(
                    IntPtr.Add(sourceAddress, y * outputBitmap.RowBytes),
                    bytes,
                    y * copiedBytesPerRow,
                    copiedBytesPerRow);
            }

            return bytes;
        }
    }
}
#endif
