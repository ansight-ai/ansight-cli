using Ansight.Host;
using Ansight.Host.Trends;

namespace Ansight.Cli.Commands.Cloud;

internal static class CloudCommands
{
    public static Task<int> RunAsync(CliArguments arguments, CliOutput output, CancellationToken cancellationToken)
    {
        if (CliCommandHelp.IsRequested(arguments))
            return Task.FromResult(CliCommandHelp.Write(output, BuildHelp(arguments.HasFlag("beta"))));

        return Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<Task<int>>("CloudCommands.RunAsync", [arguments, output, cancellationToken]);
    }

    internal static WorkspaceTrendsHistoryResult FilterTrendsHistory(WorkspaceTrendsHistoryResult history, IReadOnlyList<string> sessionIds)
        => Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<WorkspaceTrendsHistoryResult>("CloudCommands.FilterTrendsHistory", [history, sessionIds]);

    internal static CloudSessionAnalysisRunRequest CreateAnalysisRequest(Guid sessionId, CliArguments arguments, bool summaryDefaults)
        => Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<CloudSessionAnalysisRunRequest>("CloudCommands.CreateAnalysisRequest", [sessionId, arguments, summaryDefaults]);
    private static string BuildHelp(bool includeBetaFeatures)
    {
        var help = """
           Administer cloud apps, trends data, runner keys, sessions, and hosted analysis

           Usage:
             ansight cloud team list [--search <text>]
             ansight cloud app list [--team-id <uuid>]
             ansight cloud app register <app-id> --team-id <uuid> [--name <name>] [--platform <platform>]
             ansight cloud app remove <app-id> --team-id <uuid>
             ansight cloud trends upload|sync [<app-id>] [--team-id <uuid>] [--limit <count>]
             ansight cloud test upload <run-id> --team-id <uuid> [--app-id <id>]
             ansight cloud test sync [<app-id>] [--team-id <uuid>] [--limit <count>]
             ansight cloud test import <results.xml> [--format junit] [--session-id <id>]
             ansight cloud key list [--team-id <uuid>]
             ansight cloud build list --team-id <uuid> --app-id <id> [--limit <n>] [--include-archived]
             ansight cloud workspace list --team-id <uuid> --app-id <id> [--limit <n>] [--include-archived]
             ansight cloud build upload <apk|app-directory|zip> [--team-id <uuid>] [--idempotency-key <key>]
             ansight cloud workspace upload <workspace-directory|zip> [--full-workspace] [--team-id <uuid>] [--idempotency-key <key>]
             ansight cloud build get <id> [--team-id <uuid>]
             ansight cloud workspace get <id> [--team-id <uuid>]
             ansight cloud key issue [--team-id <uuid>] [--app-id <id>] [--scope <scope>]
             ansight cloud key revoke <key-id>
             ansight cloud session list [--team-id <uuid>] [--search <text>] [--include-archived]
             ansight cloud session download <cloud-session-id> [--output <archive.zip>]
             ansight cloud session archive|restore|delete <cloud-session-id>
             ansight cloud attachment list <cloud-session-id>
             ansight cloud attachment upload <cloud-session-id> <file> [--name <name>] [--notes <text>]
             ansight cloud attachment download <cloud-session-id> <attachment-id> --output <path>
             ansight cloud attachment delete <cloud-session-id> <attachment-id>
             ansight cloud analysis list <cloud-session-id>
             ansight cloud analysis run <cloud-session-id> [options]
             ansight cloud analysis summary <cloud-session-id> [options]
             ansight cloud analysis archive|restore <run-id>

           Query and upload options:
             --team-id <uuid>         Select an organisation
             --app-id <id>            Select a registered App ID
             --name <name>            Set an app, runner-key, or attachment display name
             --platform <name>        Add platform context when registering an app
             --search <text>          Search team, session, or App Graph listings
             --limit <count>          Bound local history or cloud session results
             --include-archived       Include archived cloud sessions
             --metric <key>      Filter a trends upload by metric key
             --decision <id>             Filter a trends upload by decision ID
             --span-group <name>           Filter a trends upload by span group
             --format <format>        Test import format: junit or appium-junit
             --workspace <path>       Restrict test sync to one workspace
             --full-workspace         Include repository source for remote build jobs; excludes .git/node_modules
             --app-version <value>    Add tested app-version context
             --branch <name>          Add source branch context
             --commit <sha>           Add source commit context
             --run-id <id>            Stable test-import run prefix
             --session-id <id>        Filter a Trends upload or link an imported test to a source session; repeatable
             --scope <scope>          Runner-key scope; repeatable
             --expires-at <utc>       Optional runner-key expiry
             --notes <text>           Set attachment notes
             --output <path>          Set a session or attachment download destination
             --force                  Replace an existing download

           Analysis options:
             summary                      Run the portal-compatible summary preset
             --kind <analysis|mermaid|trends>
             --mode <fast|thorough>
             --provider <openai|anthropic|gemini>
             --model <id>
             --source-part <part>       Repeat for logs, screenshots, visual_tree, metrics, annotations, artifacts
             --slice-start-ms <value>
             --slice-end-ms <value>
             --instructions <text>

           Runner environment:
             ANSIGHT_RUNNER_API_KEY / ANSIGHT_API_KEY
                                       Scoped an_run_ credential used instead of a saved user login
             ANSIGHT_TEAM_ID           Required organisation assertion for runner operations
             ANSIGHT_APP_ID            Optional App ID default for selecting local data

           Artifact operations use the signed-in admin/owner account by default. Runner API keys
           remain available for CI and require builds:write or workspaces:write; list and get
           require the matching :read scope. API-key list calls use the app bound to that key.
           Optional provenance: --repository <name> --commit <sha> --run-id <id>.
           Remote build jobs require a workspace uploaded with --full-workspace.

           Runner key scopes:
             builds:write              Upload and finalize app builds
             builds:read               Read build metadata and inspection status
             workspaces:write          Upload and finalize workspace snapshots
             workspaces:read           Read workspace metadata and inspection status
             runners:read              List registered remote runners and app-scoped jobs
             runners:write             Submit and cancel app-scoped remote jobs
             sessions:write            Upload team-only captured sessions
             trends:write              Upload local trends metrics and history
             tests:write               Upload Ansight or imported Appium/JUnit test results

           Use `ansight session share` to upload a local capture for the first time.
           Pass --json for versioned machine-readable output and --force to replace downloads.
           """;

        if (!includeBetaFeatures)
        {
            return help;
        }

        const string analysisOptionsHeading = "\nAnalysis options:";
        const string betaFeatureHelp = """

Beta feature commands:
  ansight cloud app-graph list --team-id <uuid> [--search <text>]
  ansight cloud app-graph show <graph-id> [--published]
""";
        return help.Replace(
            analysisOptionsHeading,
            betaFeatureHelp + Environment.NewLine + analysisOptionsHeading,
            StringComparison.Ordinal);
    }
}
