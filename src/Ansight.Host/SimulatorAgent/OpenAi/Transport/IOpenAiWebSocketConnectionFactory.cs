using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Ansight.Host.SimulatorAgent.OpenAi.Transport;

internal interface IOpenAiWebSocketConnectionFactory
{
    IOpenAiWebSocketConnection Create();
}
