using Ansight.Host;

namespace Ansight.Cli;

internal sealed class CliRuntimeLease : IAsyncDisposable
{
    private readonly CliDataDirectoryLock? dataLock;
    private readonly bool ownsRuntime;
    private readonly bool started;
    private bool disposed;

    private CliRuntimeLease(
        CliDataDirectoryLock? dataLock,
        RuntimeCoordinator runtime,
        bool started,
        bool ownsRuntime)
    {
        this.dataLock = dataLock;
        Runtime = runtime;
        this.started = started;
        this.ownsRuntime = ownsRuntime;
    }

    public RuntimeCoordinator Runtime { get; }

    public static async Task<CliRuntimeLease> CreateAsync(
        CliRuntimeOptions options,
        bool start,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (CliCommandContext.Current is { } commandContext)
        {
            if (!PathsEqual(commandContext.DataDirectory, options.DataDirectory))
            {
                throw new CliUsageException(
                    $"The resident host owns '{commandContext.DataDirectory}', but the command requested '{options.DataDirectory}'.");
            }

            if (start && !commandContext.Runtime.GetStatusSnapshot().IsRunning)
            {
                throw new CliHostUnavailableException("The resident Ansight host is not running.");
            }

            return new CliRuntimeLease(
                dataLock: null,
                commandContext.Runtime,
                started: false,
                ownsRuntime: false);
        }

        var dataLock = CliDataDirectoryLock.Acquire(options.DataDirectory);
        var runtime = CliRuntime.CreateHostRuntime(options);
        try
        {
            if (start)
            {
                await runtime.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            return new CliRuntimeLease(dataLock, runtime, start, ownsRuntime: true);
        }
        catch
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
            dataLock.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (!ownsRuntime)
        {
            return;
        }

        try
        {
            if (started)
            {
                using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await Runtime.StopAsync(stopTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            await Runtime.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            dataLock?.Dispose();
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            comparison);
    }
}
