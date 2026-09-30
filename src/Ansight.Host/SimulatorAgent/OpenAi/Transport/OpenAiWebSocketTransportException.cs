using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Ansight.Host.SimulatorAgent.OpenAi.Transport;

internal sealed class OpenAiWebSocketTransportException : IOException
{
    public OpenAiWebSocketTransportException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
