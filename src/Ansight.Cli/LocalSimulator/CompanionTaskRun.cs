using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Cli.Commands.App;
using Ansight.Host;
using Ansight.Host.Workspaces;
using Ansight.RemoteSimulator.Core.Devices;

namespace Ansight.Cli.LocalSimulator;

internal sealed record CompanionTaskRun(string Id, string RequestId, string DeviceId, string AppId,
    string ExecutionType, string Prompt, string Status, string Message, DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt, string? TrackingRunId, string? SessionId, CompanionTaskOutcome[] Results)
{
    public bool IsRunning => Status is "starting" or "running" or "validating" or "cancelling";
    public IReadOnlyList<CompanionTaskActivity> Activities { get; init; } = [];
}
