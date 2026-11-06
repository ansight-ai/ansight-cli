using System.Globalization;
using System.Text.Json;
using Ansight.Host;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Cli.Commands.Session;

internal sealed record SessionLocalSummaryOutput(
    string Schema,
    string SessionId,
    string AnalysisId,
    string AgentId,
    string Summary);
