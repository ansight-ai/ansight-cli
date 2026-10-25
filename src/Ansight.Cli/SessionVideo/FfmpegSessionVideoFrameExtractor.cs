using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Ansight.Host;

namespace Ansight.Cli.SessionVideo;

// Linux decoder dependency only; recording continues to use the device's native encoder.
internal sealed class FfmpegSessionVideoFrameExtractor(string executable = "ffmpeg") : ISessionVideoFrameExtractor
{
    public async Task ExtractAsync(string videoPath, TimeSpan position, string imagePath,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-nostdin", "-v", "error", "-y", "-ss",
                     position.TotalSeconds.ToString("F6", CultureInfo.InvariantCulture), "-i", videoPath,
                     "-map", "0:v:0", "-frames:v", "1", "-vf", "scale=1024:1024:force_original_aspect_ratio=decrease",
                     "-q:v", "3", "-update", "1", imagePath }) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("Video-backed session summarisation on Linux requires FFmpeg on PATH.", exception);
        }
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var details = await error.ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(imagePath))
                throw new IOException($"Video frame extraction failed: {details[..Math.Min(details.Length, 1000)]}");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            await error.ConfigureAwait(false);
        }
    }
}
