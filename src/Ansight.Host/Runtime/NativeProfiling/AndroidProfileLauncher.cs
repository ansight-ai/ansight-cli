using Ansight.Adb;

namespace Ansight.Host.Runtime.NativeProfiling;

internal sealed class AndroidProfileLauncher : IAndroidProfileLauncher
{
    public async Task LaunchAsync(
        string adbPath,
        string deviceId,
        string appId,
        CancellationToken cancellationToken)
    {
        var adb = new AdbClient(adbPath);
        await adb.LaunchApplicationForProfilingAsync(deviceId, appId, cancellationToken)
            .ConfigureAwait(false);
    }
}
