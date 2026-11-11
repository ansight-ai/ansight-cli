using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Activity;
using Ansight.Host.Runtime.Operations.Tools.DeviceLifecycle;
using Ansight.RemoteSimulator.Core.Devices;

namespace Ansight.Cli.LocalSimulator;

internal sealed partial class CliCompanionTestingSource
{
    private RemoteDeviceTestingResult ReadDeviceActivity(string deviceIdentifier)
    {
        var activities = new List<DeviceActivity>();
        CompanionTaskRun[] companionRuns;

        lock (gate)
        {
            companionRuns = history.Where(run => run.IsRunning
                && string.Equals(run.DeviceId, deviceIdentifier, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        foreach (var run in companionRuns)
        {
            activities.Add(new DeviceActivity(run.Id, run.ExecutionType == "test" ? "test" : "agent",
                run.Prompt[..Math.Min(run.Prompt.Length, 160)], run.AppId, "Ansight agent", run.StartedAt));
        }

        var activeRecords = runtime.Activity.ListActive()
            .Where(IsAgentActivity)
            .Where(record => MatchesDevice(record, deviceIdentifier))
            .Where(record => !companionRuns.Any(run => run.SessionId is not null && run.SessionId == record.SessionId))
            .GroupBy(record => record.TraceId)
            .Select(records => records.OrderBy(record => ActivityPriority(record.Category))
                .ThenBy(record => record.StartedAtUtc).First());

        foreach (var record in activeRecords)
        {
            var kind = record.Category is "test" or "task" ? record.Category : "agent";
            var title = record.DisplayName ?? (kind == "task" ? "Running task" : "Agent controlling app");
            activities.Add(new DeviceActivity(record.OperationId, kind, title, record.AppId,
                record.Caller.Name ?? "Agent", record.StartedAtUtc));
        }

        return new RemoteDeviceTestingResult(true, "Current device activity.", new JsonObject
        {
            ["activities"] = JsonSerializer.SerializeToNode(activities.OrderBy(activity => activity.StartedAtUtc), jsonOptions)
        });
    }

    private bool MatchesDevice(SessionActivityRecord record, string deviceIdentifier)
    {
        if (!string.IsNullOrWhiteSpace(record.DeviceIdentifier))
        {
            return string.Equals(record.DeviceIdentifier, deviceIdentifier, StringComparison.OrdinalIgnoreCase);
        }

        if (record.SessionId is null)
        {
            return false;
        }

        if (!runtime.Sessions.TryGetLiveContentSnapshot(record.SessionId, out var session) || session is null)
        {
            return false;
        }

        return string.Equals(DeviceLifecycleTool.ResolveNativeDeviceIdentifier(session), deviceIdentifier,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAgentActivity(SessionActivityRecord record)
    {
        var knownExecution = record.Category is "test" or "agent" or "task";
        var externalAgent = record.Caller.Kind is "agent" or "automation";

        return knownExecution || externalAgent;
    }

    private static int ActivityPriority(string category)
    {
        return category switch
        {
            "test" => 0,
            "agent" => 1,
            "task" => 2,
            _ => 3
        };
    }

    private sealed record DeviceActivity(string Id, string Kind, string Title, string? AppId,
        string Caller, DateTimeOffset StartedAtUtc);
}
