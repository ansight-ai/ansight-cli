using System.Diagnostics;
using System.Text.Json;
using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.App;

internal sealed record AppExecutionTarget(
    string? SessionId,
    string? ApplicationIdentifier,
    WorkspaceTestTargetRequest? LaunchRequest)
{
    public bool RequiresLaunch => SessionId is null;
}
