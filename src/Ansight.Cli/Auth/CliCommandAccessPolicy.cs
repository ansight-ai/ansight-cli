namespace Ansight.Cli.Auth;

internal enum CliAccessClass
{
    Bootstrap,
    ResidentHost,
    Cloud,
    Runner
}

internal static class CliCommandAccessPolicy
{
    public static CliAccessClass Classify(CliArguments arguments, bool runnerConfigured = false)
    {
        var parts = arguments.Positionals;
        if (parts.Count == 0 || CliCommandHelp.IsRequested(arguments))
        {
            return CliAccessClass.Bootstrap;
        }

        var command = parts[0].ToLowerInvariant();
        var action = parts.Count > 1 ? parts[1].ToLowerInvariant() : string.Empty;
        if (command == "host" && action == "run")
        {
            return CliAccessClass.ResidentHost;
        }
        if (command is "help" or "version" or "info" or "update" or "upgrade"
            or "doctor" or "capabilities" or "analytics" or "config" or "settings" or "setup"
            or "license" or "licenses" or "licence" or "licences" or "notice" or "notices"
            or "attribution" or "attributions")
        {
            return CliAccessClass.Bootstrap;
        }

        if (command == "auth" && action is "" or "login" or "sign-in" or "signin" or "logout" or "sign-out" or "signout" or "refresh" or "status"
            || command == "account" && action is "" or "open" or "portal" or "login" or "sign-in" or "signin"
                or "logout" or "sign-out" or "signout" or "refresh" or "status" or "access" or "grant" or "grants" or "usage" or "budget"
                or "machines"
            || command == "host" && action is "status" or "stop"
            || parts.Count == 1 && command is "companion" or "remote" or "remote-simulator")
        {
            return CliAccessClass.Bootstrap;
        }

        // These commands use scoped, organization-authorized backend APIs.
        // Local tools and host startup remain independent of cloud authorization;
        // AI model requests enforce their own scoped runner-key access at the gateway.
        if (runnerConfigured)
        {
            var operation = parts.Count > 2 ? parts[2].ToLowerInvariant() : string.Empty;
            if (command == "cloud"
                && (action is "build" or "workspace" && operation is "upload" or "get" or "list"
                    || action == "trends" && operation is "upload" or "sync"
                    || action is "test" or "tests" && operation is "upload" or "sync" or "import")
                || command == "cloud" && action is "analysis" or "analyses"
                || command is "session" or "sessions" && (action == "share"
                    || action is "summary-local" or "summarize-local" or "summarise-local"
                    || action is "summary" or "summarize" or "summarise"))
            {
                return CliAccessClass.Runner;
            }
            if ((command is "runner" or "runners")
                && (action is "machines" or "list" or "jobs" or "submit" or "enqueue" or "cancel"))
            {
                return CliAccessClass.Runner;
            }
        }

        // Local summaries use the host's retained evidence and its AI gateway, just
        // like app/test execution. Opening a second runtime would contend for the
        // resident host's storage lock. Scoped runner keys were handled above.
        if (IsLocalSessionSummary(arguments)) return CliAccessClass.Bootstrap;

        // Cloud operations require identity; local tools and unknown commands never
        // require a subscription. Unknown commands reach the normal usage validation.
        if (command == "cloud" && action is "team" or "teams" or "app" or "apps"
                or "app-graph" or "app-graphs" or "trends" or "test" or "tests"
                or "key" or "keys" or "runner-key" or "runner-keys"
                or "session" or "sessions" or "attachment" or "attachments"
                or "analysis" or "analyses" or "build" or "workspace"
            || command is "session" or "sessions" && action is "share" or "share-batch"
                or "summary" or "summarize" or "summarise"
                or "summary-local" or "summarize-local" or "summarise-local"
                or "url" or "share-url" or "replay-url"
            || command is "app-graph" or "app-graphs" && (action == "sync"
                || arguments.GetOption("graph-store")?.Trim().ToLowerInvariant() is "hosted" or "server" or "cloud")
            || command == "app" && action == "execute"
                && arguments.GetOption("graph-store")?.Trim().ToLowerInvariant() is "hosted" or "server" or "cloud"
            || command is "runner" or "runners" && action is "setup" or "register" or "revoke" or "unregister"
                or "machines" or "list" or "jobs" or "submit" or "enqueue" or "cancel" or "secret" or "secrets")
            return CliAccessClass.Cloud;

        return CliAccessClass.Bootstrap;
    }

    // AI credentials belong to the invoking process, not an already-running host.
    internal static bool UsesAiGateway(CliArguments arguments)
    {
        var parts = arguments.Positionals;
        if (parts.Count < 2) return false;
        var command = parts[0].ToLowerInvariant();
        var action = parts[1].ToLowerInvariant();
        return command == "app" && action == "execute"
            || command is "app-graph" or "app-graphs" && action is "explore" or "run"
            || command is "test" or "tests" && action is "run" or "run-all" or "run-inline" or "retry-metering"
            || command is "task" or "tasks" && action is "run" or "extract"
            || command == "replay"
            || IsLocalSessionSummary(arguments);
    }

    private static bool IsLocalSessionSummary(CliArguments arguments)
        => arguments.Positionals.Count >= 2
           && arguments.Positionals[0].ToLowerInvariant() is "session" or "sessions"
           && (arguments.Positionals[1].ToLowerInvariant() is "summary-local" or "summarize-local" or "summarise-local"
               || arguments.HasFlag("local") && arguments.Positionals[1].ToLowerInvariant() is "summary" or "summarize" or "summarise");

    internal static bool UsesRequestScopedRunnerCredentials(CliArguments arguments)
    {
        if (UsesAiGateway(arguments)) return true;
        var parts = arguments.Positionals;
        if (parts.Count < 2) return false;
        var command = parts[0].ToLowerInvariant();
        return (command is "session" or "sessions" || command == "cloud" && parts[1].ToLowerInvariant() is "analysis" or "analyses")
            && Classify(arguments, runnerConfigured: true) == CliAccessClass.Runner;
    }

    public static bool HasRunnerCredentials
        => !string.IsNullOrWhiteSpace(Ansight.Host.Workspaces.Execution.RunnerAiCredentialContext.Current?.ApiKey)
           || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANSIGHT_RUNNER_API_KEY"))
           || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANSIGHT_API_KEY"));
}
