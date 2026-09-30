using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Ansight.Host.SimulatorAgent.OpenAi.Transport;

internal interface IOpenAiSession : IAsyncDisposable
{
    SimulatorAgentTransportDiagnostics? LastTransportDiagnostics => null;

    Task WarmupAsync(
        OpenAiRequest request,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    Task<OpenAiTurn> CreateResponseAsync(
        OpenAiRequest request,
        CancellationToken cancellationToken);
}
