using Ansight.Host;

namespace Ansight.Cli.SessionVideo;

internal static class CliSessionVideoEncoderFactory
{
    public static ISessionVideoFrameExtractor CreateFrameExtractor()
    {
#if WINDOWS
        return new WindowsSessionVideoFrameExtractor();
#else
        return OperatingSystem.IsMacOS() ? new MacSessionVideoFrameExtractor() : new FfmpegSessionVideoFrameExtractor();
#endif
    }

    public static ISessionVideoEncoder? Create()
    {
#if WINDOWS
        return new WindowsSessionVideoEncoder();
#else
        return OperatingSystem.IsMacOS()
            ? new MacSessionVideoEncoder()
            : null;
#endif
    }
}
