namespace Ansight.Cli.Commands.Analytics;

internal static class AnalyticsCommands
{
    public static int Run(CliArguments arguments, CliOutput output)
    {
        if (arguments.Positionals.Count == 1 || CliCommandHelp.IsRequested(arguments))
        {
            return CliCommandHelp.Write(output, BuildHelp());
        }

        var command = arguments.RequirePositional(1, "analytics command").ToLowerInvariant();
        var settings = new AnalyticsSettingsStore(
            CliRuntime.ResolveDataDirectory(arguments.GetOption("data-dir")));
        return command switch
        {
            "status" => WriteStatus(arguments, output, settings, "status"),
            "detailed" => SetDetailedTracking(arguments, output, settings),
            "lifecycle" => RecordLifecycle(arguments, output),
            _ => throw new CliUsageException(
                $"Unknown analytics command '{command}'. Expected status or detailed.")
        };
    }

    private static int RecordLifecycle(CliArguments arguments, CliOutput output)
    {
        var action = arguments.RequirePositional(2, "lifecycle action").ToLowerInvariant();
        arguments.EnsurePositionalCount(3, "ansight analytics lifecycle install|update");
        if (action is not ("install" or "update"))
        {
            throw new CliUsageException(
                $"Unknown lifecycle action '{action}'. Expected install or update.");
        }

        output.Write(
            new AnalyticsLifecycleOutput("ansight.cli.analytics-lifecycle/v1", action),
            () => $"Recorded CLI {action} lifecycle event.");
        return CliExitCodes.Success;
    }

    private static int SetDetailedTracking(
        CliArguments arguments,
        CliOutput output,
        AnalyticsSettingsStore settings)
    {
        var action = arguments.RequirePositional(2, "detailed analytics action").ToLowerInvariant();
        arguments.EnsurePositionalCount(3, "ansight analytics detailed enable|disable");
        var enabled = action switch
        {
            "enable" => true,
            "disable" => false,
            _ => throw new CliUsageException(
                $"Unknown detailed analytics action '{action}'. Expected enable or disable.")
        };

        if (enabled && !settings.HasSignedInAccount)
            throw new CliUsageException("Sign in before opting in to detailed analytics.");

        settings.SetDetailedTrackingEnabled(enabled);
        if (!enabled)
        {
            new EventOutbox(settings.AnalyticsDirectoryPath).RemoveDetailedEvents();
        }

        return WriteStatus(arguments, output, settings, action);
    }

    private static int WriteStatus(
        CliArguments arguments,
        CliOutput output,
        AnalyticsSettingsStore settings,
        string action)
    {
        arguments.EnsurePositionalCount(
            action == "status" ? 2 : 3,
            action == "status"
                ? "ansight analytics status"
                : "ansight analytics detailed enable|disable");
        var result = new AnalyticsStatusOutput(
            "ansight.cli.analytics-status/v1",
            DailyUseTrackingEnabled: true,
            settings.IsDetailedTrackingEnabled,
            action,
            settings.SettingsPath);
        output.Write(
            result,
            () => $"Local engagement tracking: enabled (mandatory)\n"
                  + $"Detailed usage consent: {(result.DetailedTrackingEnabled ? "enabled" : "disabled")}\n"
                  + $"Detailed usage active: {(settings.IsDetailedTrackingActive ? "yes" : "no; requires sign-in and consent")}\n"
                  + "Engagement records daily active use, any CLI use at most once per 24 hours, and an hourly engaged event after two local actions. Command names are excluded.\n"
                  + "Detailed tracking adds normalized operations, outcomes, timings, local feature/recording counters, and acquisition milestones.\n"
                  + "Events are queued locally; authentication and install commands attempt a bounded delivery, and the host retries while running.\n"
                  + $"Settings: {result.SettingsPath}");
        return CliExitCodes.Success;
    }

    private static string BuildHelp()
        => """
           Inspect or change Ansight CLI analytics.

           Usage:
             ansight analytics status [--json]
             ansight analytics detailed enable|disable [--json]

           Local engagement tracking is mandatory. It records a pseudonymous
           daily active user, any CLI use at most once per 24 hours without a command name,
           and an hourly engaged event after two local actions.

           Detailed tracking requires sign-in and explicit opt-in for that account.
           It includes normalized operations, outcomes, duration,
           local recording and feature counts, estimated active UI minutes, and
           acquisition milestones. Installation/account UUIDs link supported acquisition steps.
           It never sends raw arguments, paths, app/session/test identifiers, prompts,
           logs, request bodies, or captured content through these analytics events.
           Disabling detailed tracking removes unsent detailed events and daily usage counters.
           ANSIGHT_ANALYTICS_DISABLED=true also disables detailed collection.

           The CLI retains events in a local outbox; the resident host delivers them.
           """;
}
