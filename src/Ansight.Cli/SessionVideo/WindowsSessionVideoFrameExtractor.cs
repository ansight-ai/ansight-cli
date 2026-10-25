#if WINDOWS
using System.Runtime.InteropServices.WindowsRuntime;
using Ansight.Host;
using Windows.Media.Editing;
using Windows.Storage;

namespace Ansight.Cli.SessionVideo;

internal sealed class WindowsSessionVideoFrameExtractor : ISessionVideoFrameExtractor
{
    public async Task ExtractAsync(string videoPath, TimeSpan position, string imagePath,
        CancellationToken cancellationToken = default)
    {
        var file = await StorageFile.GetFileFromPathAsync(videoPath).AsTask(cancellationToken).ConfigureAwait(false);
        var clip = await MediaClip.CreateFromFileAsync(file).AsTask(cancellationToken).ConfigureAwait(false);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        using var thumbnail = await composition.GetThumbnailAsync(position, 1024, 0, VideoFramePrecision.NearestFrame)
            .AsTask(cancellationToken).ConfigureAwait(false);
        using var input = thumbnail.AsStreamForRead();
        await using var output = File.Create(imagePath);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }
}
#endif
