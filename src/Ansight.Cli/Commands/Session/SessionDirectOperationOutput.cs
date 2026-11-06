using System.Globalization;
using System.Text.Json;
using Ansight.Host;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Cli.Commands.Session;

internal sealed record SessionDirectOperationOutput(
    string Schema,
    string Operation,
    string SessionId,
    OperationResult Result);
