using Ansight.Adb;

namespace Ansight.Host.Runtime.NativeProfiling;

internal interface IAndroidProfileLauncher
{
    Task LaunchAsync(
        string adbPath,
        string deviceId,
        string appId,
        CancellationToken cancellationToken);
}
