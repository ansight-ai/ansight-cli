using Ansight.Host;
using Ansight.Host.Trends;
using Ansight.Infrastructure;

namespace Ansight.Cli.Commands.Trends;

internal static class TrendsCommands
{
    public static Task<int> RunAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        if (CliCommandHelp.IsRequested(arguments))
        {
            return Task.FromResult(CliCommandHelp.Write(output, BuildHelp()));
        }

        var subcommand = arguments.RequirePositional(1, "trends subcommand").ToLowerInvariant();
        return subcommand switch
        {
            "history" => Task.FromResult(History(arguments, output)),
            "rebuild" => RebuildAsync(arguments, output, cancellationToken),
            _ => throw new CliUsageException(
                $"Unknown trends subcommand '{subcommand}'. Expected history or rebuild.")
        };
    }

    private static int History(CliArguments arguments, CliOutput output)
    {
        arguments.EnsurePositionalCount(2, "ansight trends history [filters]");
        var options = CliRuntime.ResolveOptions(arguments);
        var result = WorkspaceTrendsService.LoadHistory(
            new DataToolApplicationPaths(options.DataDirectory),
            arguments.GetOption("app-id"),
            arguments.GetOption("metric"),
            arguments.GetOption("decision"),
            arguments.GetIntOption("limit", 100, 1, 10_000),
            arguments.GetOption("span-group"));
        output.Write(
            new TrendsHistoryOutput("ansight.trends-history/v1", result),
            () => RenderHistory(result));
        return CliExitCodes.Success;
    }

    private static async Task<int> RebuildAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(2, "ansight trends rebuild [filters]");
        var appId = arguments.GetOption("app-id");
        var appVersion = arguments.GetOption("app-version");
        var workspacePath = arguments.GetOption("workspace");
        if (!string.IsNullOrWhiteSpace(appVersion) && string.IsNullOrWhiteSpace(appId))
        {
            throw new CliUsageException("--app-version requires --app-id so the rebuild target is unambiguous.");
        }
        if (!string.IsNullOrWhiteSpace(workspacePath) && string.IsNullOrWhiteSpace(appId))
        {
            throw new CliUsageException("--workspace requires --app-id and overrides that app's registered codebase.");
        }

        var options = CliRuntime.ResolveOptions(arguments);
        await using var lease = await CliRuntimeLease.CreateAsync(
            options,
            start: false,
            cancellationToken).ConfigureAwait(false);
        var result = lease.Runtime.Trends.RebuildHistory(
            new WorkspaceTrendsHistoryRebuildRequest(
                appId,
                appVersion,
                arguments.GetOption("metric"),
                workspacePath,
                arguments.HasFlag("dry-run")),
            cancellationToken);
        output.Write(
            new TrendsRebuildOutput("ansight.trends-rebuild/v1", result),
            () => RenderRebuild(result));
        return result.Apps.Count == 0 && !string.IsNullOrWhiteSpace(appId)
            ? CliExitCodes.Failure
            : CliExitCodes.Success;
    }

    internal static string RenderHistory(WorkspaceTrendsHistoryResult result)
    {
        var lines = new List<string>
        {
            $"Trends history: {result.Metrics.Count:N0} metric(s), {result.History.Count:N0} comparison(s)",
            $"Database: {result.DatabasePath}"
        };
        lines.AddRange(result.Metrics.Select(metric =>
            $"METRIC\t{metric.EvaluatedAtUtc:O}\t{metric.AppId}\t"
            + $"{metric.MetricKey}\t{metric.Value:0.###} {metric.Unit}\t"
            + $"{metric.Status}\tspan-group={metric.SpanGroup ?? "ungrouped"}"
            + $"\tapp-version={metric.AppVersion ?? "unknown"}"));
        lines.AddRange(result.History.Select(entry =>
            $"HISTORY\t{entry.EvaluatedAtUtc:O}\t{entry.AppId}\t{entry.DecisionId}\t"
            + $"{entry.Status}\tcurrent={entry.CurrentValue:0.###}\t"
            + $"baseline={(entry.BaselineValue.HasValue ? entry.BaselineValue.Value.ToString("0.###") : "warmup")}"
            + $"\tbaseline-runs={entry.BaselineRunCount}/{entry.MinimumBaselineRunCount}"
            + $"\tspan-group={entry.SpanGroup ?? "ungrouped"}"
            + $"\tcomparison={entry.Comparison}"
            + $"\tapp-version={entry.AppVersion ?? "unknown"}"
            + $"\tbaseline-app-version={entry.BaselineAppVersion ?? "none"}"));
        return string.Join(Environment.NewLine, lines);
    }

    internal static string RenderRebuild(WorkspaceTrendsHistoryRebuildResult result)
    {
        var verb = result.DryRun ? "Would rebuild" : "Rebuilt";
        var lines = new List<string>
        {
            $"{verb} {result.RebuiltHistoryResultCount:N0} trends comparison(s) across {result.Apps.Count:N0} app(s); "
            + $"{(result.DryRun ? "would replace" : "replaced")} {result.RemovedHistoryResultCount:N0} stored result(s).",
            $"Database: {result.DatabasePath}"
        };
        lines.AddRange(result.Apps.Select(app =>
            $"APP\t{app.AppId}\tmetrics={app.MetricCount:N0}\t"
            + $"rules={app.HistoryDefinitionCount:N0}\tresults={app.HistoryResultCount:N0}\t"
            + $"workspace={app.WorkspacePath}"));
        lines.AddRange(result.SkippedApps.Select(app => $"SKIPPED\t{app.AppId}\t{app.Reason}"));
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildHelp()
        => """
           Inspect and rebuild deterministic trends history

           Usage:
             ansight trends history [filters]
             ansight trends rebuild [filters]

           Commands:
             history   List stored trends metrics and historical comparisons
             rebuild   Re-evaluate stored metrics with the current workspace trends rules

           Filters:
             --app-id <id>                Restrict results to one application ID
             --metric <key>               Restrict results to trends-id.metric-id
             --decision <id>              Restrict history to one comparison decision ID
             --span-group <name>        Restrict results to one grouped span value
             --limit <count>              Maximum metrics and history rows; default: 100

           Rebuild options:
             --app-id <id>                Rebuild one app; omit to rebuild all apps with metrics
             --app-version <version>      Rebuild only this version of the selected app
             --metric <key>               Rebuild only one metric's regression policy
             --workspace <path>           Override the selected app's registered codebase
             --dry-run                    Preview replacements without changing stored history

           Output options:
             --json                       Emit versioned machine-readable JSON
             --silent                     Suppress output
           """;
}

internal sealed record TrendsHistoryOutput(
    string Schema,
    WorkspaceTrendsHistoryResult History);

internal sealed record TrendsRebuildOutput(
    string Schema,
    WorkspaceTrendsHistoryRebuildResult Rebuild);
