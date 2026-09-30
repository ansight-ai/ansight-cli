using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Ansight.Host.SimulatorAgent.OpenAi.Transport;

internal interface IOpenAiWebSocketConnection : IAsyncDisposable
{
    WebSocketState State { get; }

    Task ConnectAsync(Uri endpoint, string apiKey, CancellationToken cancellationToken);

    Task SendTextAsync(string message, CancellationToken cancellationToken);

    Task<string?> ReceiveTextAsync(CancellationToken cancellationToken);
}
