using System.Diagnostics;
using System.Text.Json;
using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.App;

internal sealed record AppExecutionBatchOutput(
    string Schema,
    DateTimeOffset CompletedUtc,
    int ExitCode,
    IReadOnlyList<AppExecutionTargetRunOutput> Runs);
