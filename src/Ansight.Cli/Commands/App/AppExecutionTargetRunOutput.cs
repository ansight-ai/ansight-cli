using System.Diagnostics;
using System.Text.Json;
using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.App;

internal sealed record AppExecutionTargetRunOutput(
    int TargetIndex,
    string? DeviceIdentifier,
    int ExitCode,
    string Message,
    AppExecutionOutput? Execution,
    IReadOnlyList<string> Diagnostics);
