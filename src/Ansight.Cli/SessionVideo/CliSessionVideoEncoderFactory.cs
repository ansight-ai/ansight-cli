using Ansight.Host;

namespace Ansight.Cli.SessionVideo;

internal static class CliSessionVideoEncoderFactory
{
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
