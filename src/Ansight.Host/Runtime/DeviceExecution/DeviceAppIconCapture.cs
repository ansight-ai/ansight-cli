using Ansight.Pairing.Models;
using SkiaSharp;

namespace Ansight.Host.Runtime.DeviceExecution;

internal static class DeviceAppIconCapture
{
    private const int maximumOutputBytes = 2 * 1024 * 1024;

    public static async Task<DeviceApplicationIconProfile?> CaptureAsync(
        WorkspaceTestTarget target, IDeviceCommandRunner commands, string? adbPath,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        DeviceCommandResult result;
        if (target.Platform == DevicePlatforms.Android)
        {
            if (string.IsNullOrWhiteSpace(adbPath)) return null;
            var user = await commands.RunAsync(adbPath,
                ["-s", target.DeviceIdentifier, "shell", "am get-current-user"], deadline.Token).ConfigureAwait(false);
            if (!int.TryParse(user.RequireText(), out var userId) || userId < 0)
                throw new IOException("The current Android user could not be resolved for icon capture.");
            var localPath = Path.Combine(Path.GetTempPath(), $"ansight-app-icon-{Guid.NewGuid():N}.dex");
            var remotePath = $"/data/local/tmp/ansight-app-icon-{Guid.NewGuid():N}.dex";
            try
            {
                await using (var resource = typeof(DeviceAppIconCapture).Assembly.GetManifestResourceStream(
                    "Ansight.Host.Runtime.DeviceExecution.Icons.android-app-icon.dex")
                    ?? throw new IOException("The Android icon helper is unavailable."))
                await using (var file = File.Create(localPath))
                    await resource.CopyToAsync(file, deadline.Token).ConfigureAwait(false);
                (await commands.RunAsync(adbPath, ["-s", target.DeviceIdentifier, "push", localPath, remotePath],
                    deadline.Token).ConfigureAwait(false)).RequireText();
                var command = $"CLASSPATH={DeviceCommandRunner.Quote(remotePath)} app_process / ai.ansight.host.AndroidAppIcon "
                    + DeviceCommandRunner.Quote(target.ApplicationIdentifier) + " " + userId;
                result = await commands.RunAsync(adbPath, ["-s", target.DeviceIdentifier, "exec-out", command],
                    deadline.Token, maximumOutputBytes).ConfigureAwait(false);
            }
            finally
            {
                File.Delete(localPath);
                // Clean up even when the capture deadline expired; keep cleanup independently bounded.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await commands.RunAsync(adbPath, ["-s", target.DeviceIdentifier, "shell", "rm -f " + DeviceCommandRunner.Quote(remotePath)], cleanup.Token).ConfigureAwait(false); }
                catch (Exception) { /* A disconnected emulator must not block session capture. */ }
            }
        }
        else
        {
            var helper = Path.Combine(AppContext.BaseDirectory, "ansight-app-icon-ios-simulator");
            if (!File.Exists(helper)) return null;
            result = await commands.RunAsync("/usr/bin/xcrun",
                ["simctl", "spawn", target.DeviceIdentifier, helper, target.ApplicationIdentifier],
                deadline.Token, maximumOutputBytes).ConfigureAwait(false);
        }
        return Decode(result.RequireText());
    }

    internal static DeviceApplicationIconProfile? Decode(string output)
    {
        var line = output.Split('\n').LastOrDefault(candidate => candidate.StartsWith("ansight-icon:", StringComparison.Ordinal));
        if (line is null) return null;
        var bytes = Convert.FromBase64String(line["ansight-icon:".Length..].Trim());
        using var codec = SKCodec.Create(new MemoryStream(bytes));
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0
            || codec.Info.Width > 2048 || codec.Info.Height > 2048) return null;
        using var bitmap = SKBitmap.Decode(codec);
        if (bitmap is null) return null;
        var width = Math.Min(256, bitmap.Width);
        var height = Math.Max(1, (int)Math.Round(bitmap.Height * (double)width / bitmap.Width));
        using var resized = bitmap.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear));
        if (resized is null) return null;
        using var png = resized.Encode(SKEncodedImageFormat.Png, 100);
        var normalized = png.ToArray();
        return new DeviceApplicationIconProfile
        {
            Format = "png", MimeType = "image/png", Width = width, Height = height,
            ByteCount = normalized.Length, DataBase64 = Convert.ToBase64String(normalized)
        };
    }
}
