using System.Diagnostics;
using System.Text.Json;
using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.App;

internal sealed record InlineAgentRunOutput(
    string Schema,
    DateTimeOffset CompletedUtc,
    string SessionId,
    string AppId,
    SimulatorAgentRunResult Result);
