using System.Text.Json;

namespace Ansight.Cli.LocalSimulator;

internal interface ICodexRpcConnection : IAsyncDisposable
{
    Task<JsonElement> CallAsync(string method, object parameters, CancellationToken cancellationToken);
}
