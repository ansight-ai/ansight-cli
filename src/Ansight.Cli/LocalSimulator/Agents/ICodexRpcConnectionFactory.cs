namespace Ansight.Cli.LocalSimulator;

internal interface ICodexRpcConnectionFactory
{
    Task<ICodexRpcConnection> ConnectAsync(CancellationToken cancellationToken);

    Task QueueAsync(string threadId, string prompt, CancellationToken cancellationToken);
}
