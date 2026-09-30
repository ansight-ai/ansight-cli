using Ansight.SimCtl.Processes;

namespace Ansight.SimCtl;

public static class SimCtlProcessLauncher
{
    private static Func<ISimCtlProcessLauncher>? configuredFactory;

    public static void ConfigureFactory(Func<ISimCtlProcessLauncher> processLauncherFactory)
    {
        ArgumentNullException.ThrowIfNull(processLauncherFactory);
        Volatile.Write(ref configuredFactory, processLauncherFactory);
    }

    internal static ISimCtlProcessLauncher Create()
        => Volatile.Read(ref configuredFactory)?.Invoke()
           ?? new DotNetProcessLauncher();
}
