using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Ansight.Cli.Analytics;

internal sealed class CliAnalytics
{
    private readonly AnalyticsSettingsStore settings;
    private readonly EventOutbox outbox;
    private readonly ProductAnalytics analytics;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly string distinctId;
    private readonly string? accountId;
    private readonly string? businessId;

    internal CliAnalytics(string dataDirectory, Func<DateTimeOffset>? utcNow = null, string? businessId = null)
    {
        analytics = new ProductAnalytics(dataDirectory);
        accountId = AnalyticsAccount.Read(dataDirectory);
        this.businessId = Guid.TryParse(businessId, out var business) ? business.ToString("D") : null;
        settings = new AnalyticsSettingsStore(dataDirectory);
        outbox = new EventOutbox(settings.AnalyticsDirectoryPath);
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        distinctId = AnalyticsIdentity.LoadOrCreate(settings.AnalyticsDirectoryPath);
    }

    public static CliAnalytics? TryCreate(CliArguments arguments)
    {
        try
        {
            return new CliAnalytics(CliRuntime.ResolveDataDirectory(arguments.GetOption("data-dir")), businessId: arguments.GetOption("team-id") ?? Environment.GetEnvironmentVariable("ANSIGHT_TEAM_ID"));
        }
        catch
        {
            return null;
        }
    }

    public Task TrackInvocationAsync(CliArguments arguments)
        => TrackCompletionAsync(arguments, CliExitCodes.Success, TimeSpan.Zero);

    public Task TrackCompletionAsync(
        CliArguments arguments,
        int exitCode,
        TimeSpan duration)
    {
        try
        {
            using var actor = AnalyticsAccount.Capture(analytics.DataDirectoryPath, accountId, businessId);
            var now = utcNow().ToUniversalTime();
            if (exitCode == CliExitCodes.Success && TryResolveLifecycle(arguments, out var lifecycle))
            {
                outbox.TryStore(CreateLifecycleEnvelope(now, arguments, lifecycle));
                analytics.TrackAcquisition(
                    lifecycle == "install" ? "cli_install_attributed" : "cli_update_attributed", once: lifecycle == "install");
            }

            if (IsMeaningfulActivity(arguments, exitCode) && !IsContinuousIntegration())
            {
                EngagementAnalytics.RecordDailySignal(analytics.DataDirectoryPath, CreateDailyEnvelope(now));
                EngagementAnalytics.RecordDailySignal(analytics.DataDirectoryPath, CreateCommandUseEnvelope(now));
                EngagementAnalytics.RecordAction(analytics.DataDirectoryPath, now);
            }

            if (settings.IsDetailedTrackingActive && !IsDetailedTrackingMutation(arguments))
            {
                outbox.TryStore(CreateDetailedEnvelope(arguments, exitCode, duration, now));
                var feature = ResolveUsageFeature(arguments);
                if (feature is not null && !CliCommandHelp.IsRequested(arguments))
                    analytics.RecordUsage(feature, "cli", exitCode switch
                    {
                        CliExitCodes.Success => "succeeded", CliExitCodes.Cancelled => "cancelled",
                        CliExitCodes.TelemetryAnalysisDetected => "finding",
                        CliExitCodes.AccessDenied or CliExitCodes.CapabilityUnavailable or CliExitCodes.Configuration => "blocked",
                        _ => "failed"
                    }, duration.TotalSeconds);
                analytics.Flush();
            }
            else if (!settings.IsDetailedTrackingActive)
            {
                outbox.RemoveDetailedEvents();
            }
        }
        catch
        {
            // Analytics must never affect CLI behavior or output.
        }

        return Task.CompletedTask;
    }

    private EventEnvelope CreateDailyEnvelope(DateTimeOffset now)
    {
        var date = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new EventEnvelope(
            Hash($"{distinctId}:cli_daily_active:{date}"),
            "cli_daily_active",
            distinctId,
            EventLevel.Daily,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["surface"] = "cli",
                ["tracking_level"] = "daily",
                ["installation_id"] = distinctId
            },
            now);
    }

    private EventEnvelope CreateCommandUseEnvelope(DateTimeOffset now)
    {
        var date = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new EventEnvelope(
            Hash($"{distinctId}:cli_command_used_daily:{date}"),
            "cli_command_used_daily",
            distinctId,
            EventLevel.Daily,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["surface"] = "cli",
                ["tracking_level"] = "engagement",
                ["installation_id"] = distinctId
            },
            now);
    }

    private EventEnvelope CreateDetailedEnvelope(
        CliArguments arguments,
        int exitCode,
        TimeSpan duration,
        DateTimeOffset now)
    {
        var identity = CliReleaseIdentity.Current;
        var command = ResolveCommand(arguments);
        return new EventEnvelope(
            Guid.NewGuid().ToString("N"),
            "cli_command_completed",
            distinctId,
            EventLevel.Detailed,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["surface"] = "cli",
                ["tracking_level"] = "detailed",
                ["installation_id"] = distinctId,
                ["command"] = command,
                ["action"] = ResolveAction(arguments),
                ["operation"] = ResolveOperation(arguments),
                ["duration_seconds"] = Math.Clamp(duration.TotalSeconds, 0, 7 * 86400),
                ["analytics_schema"] = 3,
                ["account_id"] = accountId,
                ["business_id"] = businessId,
                ["category"] = ResolveCategory(command),
                ["feature_scope"] = ResolveFeatureScope(arguments),
                ["outcome"] = exitCode == CliExitCodes.Success ? "succeeded" : "failed",
                ["exit_class"] = ResolveExitClass(exitCode),
                ["duration_bucket"] = ResolveDurationBucket(duration),
                ["cli_version"] = identity.Version,
                ["cli_build_number"] = identity.BuildNumber,
                ["is_ci"] = IsContinuousIntegration(),
                ["is_interactive"] = !Console.IsInputRedirected
            },
            now);
    }

    private EventEnvelope CreateLifecycleEnvelope(
        DateTimeOffset now,
        CliArguments arguments,
        string lifecycle)
    {
        var identity = CliReleaseIdentity.Current;
        return new EventEnvelope(
            Hash($"{distinctId}:cli_{lifecycle}:{identity.Version}:{identity.BuildNumber}"),
            lifecycle == "install" ? "cli_installed" : "cli_updated",
            distinctId,
            EventLevel.Daily,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["surface"] = "cli",
                ["tracking_level"] = "lifecycle",
                ["funnel_version"] = 1,
                ["is_ci"] = IsContinuousIntegration(),
                ["distribution_method"] = arguments.GetOption("distribution") is "shell" or "powershell" ? arguments.GetOption("distribution") : "unknown",
                ["installation_id"] = distinctId,
                ["cli_version"] = identity.Version,
                ["cli_build_number"] = identity.BuildNumber,
                ["channel"] = NormalizeLifecycleProperty(arguments.GetOption("channel")),
                ["rid"] = NormalizeLifecycleProperty(arguments.GetOption("rid")),
                ["os_platform"] = ResolveOsPlatform(),
                ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
            },
            now);
    }

    private static string ResolveCommand(CliArguments arguments)
    {
        if (arguments.Positionals.Count == 0)
        {
            return arguments.HasFlag("version") ? "version" : "help";
        }

        return arguments.Positionals[0].ToLowerInvariant() switch
        {
            "tests" => "test",
            "sessions" => "session",
            "devices" => "device",
            "apps" => "app",
            "app-graphs" => "app-graph",
            "settings" => "config",
            "workspaces" => "workspace",
            "tasks" => "task",
            "repository" => "repo",
            "account" => "auth",
            "secrets" => "secret",
            "profiling" => "profile",
            "upgrade" => "update",
            "info" => "version",
            "capabilities" => "doctor",
            "remote" or "remote-simulator" => "companion",
            "enrollment" => "pairing",
            "licenses" or "licence" or "licences" or "notice" or "notices"
                or "attribution" or "attributions" => "license",
            var command => IsKnownCommand(command) ? command : "other"
        };
    }

    private static string ResolveAction(CliArguments arguments)
    {
        if (arguments.Positionals.Count < 2)
        {
            return "default";
        }

        var action = arguments.Positionals[1].ToLowerInvariant() switch
        {
            "sign-in" or "signin" => "login",
            "sign-out" or "signout" => "logout",
            var value => value
        };
        return knownActions.Contains(action) ? action : "other";
    }


    internal static string ResolveOperation(CliArguments arguments)
    {
        var command = ResolveCommand(arguments);
        var action = ResolveAction(arguments);
        // Only these command groups have a third classification token. Never send a positional ID/path.
        var nested = command is "profile" or "cloud" || command == "app-graph" && action == "sync";
        var third = nested && arguments.Positionals.Count > 2 ? arguments.Positionals[2].ToLowerInvariant() : null;
        if (command == "replay") return command + "." + action + (arguments.HasFlag("plan-only") ? ".plan" : ".run");
        return command + "." + action + (third is not null && knownActions.Contains(third) ? "." + third : "");
    }

    private static string? ResolveUsageFeature(CliArguments arguments)
    {
        var command = ResolveCommand(arguments);
        var action = ResolveAction(arguments);
        return command switch
        {
            "session" => action switch
            {
                "logs" => "logs", "network" or "requests" => "network", "trees" => "visual_tree",
                "artifacts" => "artifacts", "annotations" => "annotations", "export" => "export", "import" => "import",
                "share" or "share-batch" => "share", _ => "session"
            },
            "app-graph" => action switch { "run" => "graph_run", "explore" => "graph_explore", _ => "app_graph" },
            "task" => action == "extract" ? "task_extract" : "task",
            "test" => action == "run-all" ? "test_batch" : "test",
            "replay" => arguments.HasFlag("plan-only") ? "replay_plan" : "replay",
            "repo" => "workspace", "auth" => "account", "config" => "settings",
            _ => ProductUsage.Features.Contains(command) ? command : null
        };
    }

    private static string ResolveCategory(string command)
        => command switch
        {
            "test" or "replay" => "tests",
            "session" or "serve" or "host" => "sessions",
            "profile" => "profiling",
            "device" or "input" or "ui" or "keyboard" or "app" or "pairing" or "companion" => "devices_and_apps",
            "task" or "repo" or "workspace" or "app-graph" => "automation",
            "audio" => "devices_and_apps",
            "trends" => "analysis",
            "config" => "configuration",
            "auth" or "secret" => "account",
            "version" or "update" => "installation",
            "analytics" => "analytics",
            "doctor" => "diagnostics",
            "license" => "legal",
            _ => "other"
        };

    private static string ResolveFeatureScope(CliArguments arguments)
    {
        var command = ResolveCommand(arguments);
        if (command is "cloud" or "auth" or "companion")
        {
            return "cloud";
        }

        return command == "session"
               && ResolveAction(arguments) is "share" or "share-batch" or "summary" or "summarize" or "summarise"
                   or "summary-local" or "summarize-local" or "summarise-local"
                   or "url" or "share-url" or "replay-url"
            ? "cloud"
            : "local";
    }

    private static string ResolveExitClass(int exitCode)
        => exitCode switch
        {
            CliExitCodes.Success => "success",
            CliExitCodes.Usage => "usage",
            CliExitCodes.HostUnavailable => "host_unavailable",
            CliExitCodes.Cancelled => "cancelled",
            CliExitCodes.TestFailed => "test_failed",
            CliExitCodes.Configuration => "configuration",
            CliExitCodes.CapabilityUnavailable => "capability_unavailable",
            CliExitCodes.AccessDenied => "access_denied",
            CliExitCodes.TelemetryAnalysisDetected => "finding",
            _ => "failure"
        };

    private static string ResolveDurationBucket(TimeSpan duration)
        => duration.TotalMilliseconds switch
        {
            < 250 => "lt_250ms",
            < 1_000 => "250ms_1s",
            < 5_000 => "1s_5s",
            < 30_000 => "5s_30s",
            < 120_000 => "30s_2m",
            _ => "gte_2m"
        };

    private static bool IsMeaningfulActivity(CliArguments arguments, int exitCode)
    {
        if (exitCode != CliExitCodes.Success || arguments.Positionals.Count == 0)
        {
            return false;
        }

        var command = ResolveCommand(arguments);
        if (command is "help" or "version" or "update" or "analytics" or "auth" or "license" or "doctor")
        {
            return false;
        }

        return !CliCommandHelp.IsRequested(arguments);
    }

    private static bool IsDetailedTrackingMutation(CliArguments arguments)
        => TryResolveLifecycle(arguments, out _)
           || arguments.Positionals.Count >= 3
           && string.Equals(arguments.Positionals[0], "analytics", StringComparison.OrdinalIgnoreCase)
           && string.Equals(arguments.Positionals[1], "detailed", StringComparison.OrdinalIgnoreCase)
           && arguments.Positionals[2].ToLowerInvariant() is "enable" or "disable";

    internal static bool IsLifecycleInvocation(CliArguments arguments)
        => TryResolveLifecycle(arguments, out _);

    private static bool TryResolveLifecycle(CliArguments arguments, out string lifecycle)
    {
        lifecycle = string.Empty;
        if (arguments.Positionals.Count < 3
            || !string.Equals(arguments.Positionals[0], "analytics", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(arguments.Positionals[1], "lifecycle", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        lifecycle = arguments.Positionals[2].ToLowerInvariant();
        return lifecycle is "install" or "update";
    }

    private static string NormalizeLifecycleProperty(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var normalized = new string(value.Trim().ToLowerInvariant()
            .Where(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            .Take(64)
            .ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
    }

    private static bool IsKnownCommand(string command)
        => knownCommands.Contains(command);

    private static bool IsContinuousIntegration()
        => ReadBooleanEnvironmentVariable("CI")
           || ReadBooleanEnvironmentVariable("GITHUB_ACTIONS")
           || ReadBooleanEnvironmentVariable("TF_BUILD")
           || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BUILD_BUILDID"));

    private static bool ReadBooleanEnvironmentVariable(string name)
        => Environment.GetEnvironmentVariable(name)?.Trim().ToLowerInvariant()
            is "1" or "true" or "yes";

    private static string ResolveOsPlatform()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            return "macos";
        }

        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux";
        }

        return "other";
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes[..16]).ToLowerInvariant();
    }

    private static readonly HashSet<string> knownCommands = new(StringComparer.Ordinal)
    {
        "app-graph", "replay", "audio", "trends", "config", "analytics", "app", "auth", "cloud", "companion", "device", "doctor", "help", "host", "input",
        "keyboard", "license", "pairing", "profile", "repo", "secret", "serve", "session", "ui",
        "task", "test", "update", "version", "workspace"
    };

    private static readonly HashSet<string> knownActions = new(StringComparer.Ordinal)
    {
        "network", "requests", "telemetry", "screenshots", "artifacts", "compare", "extract", "execute", "explore", "sync", "push", "pull", "rebuild", "inspect", "run-inline", "inject", "capabilities", "ios", "android", "sample", "sentry", "posthog", "ansight", "call-tree", "manifest", "speedscope", "access", "add", "analyses", "annotations", "apps", "audit", "cache", "call", "cancel",
        "check", "connections", "cpu", "delete", "detailed", "disconnect", "dotnet", "enable",
        "exceptions", "export", "gc", "get", "help", "history", "images", "import", "init", "install",
        "jit", "launch", "list", "location", "login", "logout", "logs", "machines", "metadata", "metrics",
        "dismiss", "is-open", "open", "openai", "overview", "pair", "plan", "prune", "push-file", "refresh", "register", "remove", "run",
        "run-all", "sanitize", "screenshot", "serve", "share", "share-batch", "share-url", "replay-url",
        "show", "shutdown", "start", "startup", "summary", "summarize", "summarise",
        "summary-local", "summarize-local", "summarise-local",
        "status", "stop", "terminate", "threads", "tools", "touches", "trees", "url", "validate"
    };
}
