namespace Ansight.Cli.LocalSimulator;

internal interface ICodexRpcConnectionFactory
{
    bool UsesDesktopDelivery => false;

    Task OpenDesktopAsync(string threadId, CancellationToken cancellationToken) => Task.CompletedTask;

    Task<ICodexRpcConnection> ConnectAsync(CancellationToken cancellationToken);

    Task QueueAsync(string threadId, string prompt, CancellationToken cancellationToken);
}
