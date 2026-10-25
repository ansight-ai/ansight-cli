using System.Runtime.InteropServices;
using System.Text;
using Ansight.Host;

namespace Ansight.Cli.SessionVideo;

// Finalization is intentionally independent of the run's cancellation token.
internal sealed class MacSessionVideoTranscoder : ISessionVideoTranscoder
{
    public Task TranscodeAsync(string sourcePath, string outputPath, TimeSpan duration) => Task.Run(() =>
    {
        var error = new StringBuilder(4096);
        if (Transcode(sourcePath, outputPath, 1280, 24, 1_200_000, duration.Ticks / 10, error, error.Capacity) == 0)
            throw new IOException($"Native video compression failed: {error}");
    });

    [DllImport("libAnsightSessionVideoMac.dylib", EntryPoint = "AnsightSessionVideoMac_Transcode")]
    private static extern int Transcode(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sourcePath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string outputPath,
        int maxDimension, int framesPerSecond, int bitRate, long durationUs,
        [Out] StringBuilder error, int errorCapacity);
}
