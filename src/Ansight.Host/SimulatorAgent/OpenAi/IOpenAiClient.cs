using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ansight.Host.SimulatorAgent.OpenAi;

internal interface IOpenAiClient : IDisposable
{
    Task<OpenAiTurn> CreateResponseAsync(
        OpenAiRequest request,
        CancellationToken cancellationToken);
}
