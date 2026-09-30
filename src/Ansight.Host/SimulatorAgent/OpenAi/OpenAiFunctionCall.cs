using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ansight.Host.SimulatorAgent.OpenAi;

internal sealed record OpenAiFunctionCall(
    string CallId,
    string Name,
    JsonObject Arguments);
