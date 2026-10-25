namespace Ansight.Cli.Commands;

internal static class RunVideoOptions
{
    // Null preserves the saved test's setting; an explicit off overrides it.
    public static TestVideoOptions? Resolve(CliArguments arguments)
    {
        if (arguments.HasFlag("video"))
            throw new CliUsageException("Video capture is enabled by default. --video has been removed; use --no-video to disable recording.");
        if (arguments.GetOptions("no-video").Count > 0)
            throw new CliUsageException("--no-video does not accept a value.");

        if (arguments.HasFlag("video-retention"))
        {
            var retention = arguments.RequireOption("video-retention");
            if (retention is not ("always" or "on-failure" or "off"))
                throw new CliUsageException("--video-retention must be always, on-failure, or off.");
            if (arguments.HasFlag("no-video") && retention != "off")
                throw new CliUsageException("--no-video cannot be combined with recording retention. Remove --video-retention or set it to off.");
            return new TestVideoOptions(retention);
        }

        return arguments.HasFlag("no-video") ? new TestVideoOptions("off") : null;
    }

    public static string Render(TestVideoResult? video)
        => video is null ? string.Empty
            : $"{Environment.NewLine}Video: {video.Status} — {video.Message}"
              + (video.RecordingId is null ? string.Empty
                  : $"{Environment.NewLine}Replay video: {video.RecordingId} ({video.ByteSize:N0} bytes)");
}
