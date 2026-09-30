namespace Ansight.Host.Devices;

/// <summary>
/// Carries a command's window preference into indirect device launches and background work
/// without changing the defaults for other commands sharing the resident host.
/// </summary>
public static class DeviceLaunchContext
{
    private static readonly AsyncLocal<bool> headless = new();

    public static bool Headless => headless.Value;

    public static IDisposable Push(bool headless)
    {
        var previous = DeviceLaunchContext.headless.Value;
        DeviceLaunchContext.headless.Value = headless;
        return new Scope(previous);
    }

    private sealed class Scope(bool previous) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            headless.Value = previous;
        }
    }
}
