using System.Runtime.InteropServices;
using System.Text;
using Ansight.Host;

namespace Ansight.Cli.SessionVideo;

internal sealed class MacSessionVideoFrameExtractor : ISessionVideoFrameExtractor
{
    public async Task ExtractAsync(string videoPath, TimeSpan position, string imagePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(() =>
        {
            var error = new StringBuilder(4096);
            if (Extract(videoPath, imagePath, position.Ticks / 10, 1024, error, error.Capacity) == 0)
                throw new IOException($"Native video frame extraction failed: {error}");
        }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    [DllImport("libAnsightSessionVideoMac.dylib", EntryPoint = "AnsightSessionVideoMac_ExtractFrame")]
    private static extern int Extract(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string videoPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string imagePath,
        long positionUs, int maxDimension, [Out] StringBuilder error, int errorCapacity);
}
