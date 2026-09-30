namespace Ansight.Cli;

internal static class CliRequirementGuidance
{
    public static string Append(string message, string requirement)
    {
        var suggestions = BuildSuggestions(requirement);
        if (suggestions.Count == 0)
        {
            return message;
        }

        return string.Join(
            Environment.NewLine,
            new[] { message, "How to find or supply it:" }
                .Concat(suggestions.Select(static suggestion => $"  {suggestion}")));
    }

    private static IReadOnlyList<string> BuildSuggestions(string requirement)
    {
        var normalized = requirement.Trim().ToLowerInvariant();
        var suggestions = new List<string>();

        if (ContainsAny(normalized, "application identifier", "application id")
            || (normalized.Contains("ansight device", StringComparison.Ordinal)
                && ContainsAny(normalized, "<app-id>", "<application-id>")))
        {
            suggestions.Add("List targets: ansight device list");
            suggestions.Add("List installed application IDs: ansight device apps <ios|android> <device-id>");
        }
        else if (ContainsAny(normalized, "app identifier", "app id", "app-id", "--app-id"))
        {
            suggestions.Add("List known app IDs: ansight app list");
            suggestions.Add("An app ID is normally its bundle/package identifier, for example com.example.app.");
        }

        if (ContainsAny(normalized, "device identifier", "device id", "device-id", "--device-id"))
        {
            suggestions.Add("List simulator and emulator IDs: ansight device list");
        }

        if (ContainsAny(normalized, "session identifier", "session id", "session-id", "--session-id"))
        {
            suggestions.Add("List recorded and connected session IDs: ansight session list");
        }

        if (ContainsAny(normalized, "tool identifier", "tool id", "tool-id"))
        {
            suggestions.Add("List tools exposed by a connected session: ansight app tools <session-id>");
        }

        if (ContainsAny(normalized, "test identifier", "test id", "test-id"))
        {
            suggestions.Add("List workspace test IDs: ansight test list <workspace-path>");
        }

        if (ContainsAny(normalized, "secret alias", "secret-alias"))
        {
            suggestions.Add("List configured secret aliases: ansight secret list <app-id>");
        }

        if (ContainsAny(normalized, "invite identifier", "invite id", "invite-id"))
        {
            suggestions.Add("List enrollment invite IDs: ansight pairing list");
        }

        if (ContainsAny(normalized, "capture identifier", "capture id", "capture-id"))
        {
            suggestions.Add("List .NET profiling capture IDs: ansight profile dotnet list");
            suggestions.Add("List native profiling capture IDs: ansight profile ios list or ansight profile android list");
            suggestions.Add("List local process samples: ansight profile sample list");
        }

        if (normalized.Contains("workspace path", StringComparison.Ordinal))
        {
            suggestions.Add("Use the repository root containing ansight/, or initialize one: ansight workspace init <workspace-path>");
        }

        if (normalized.Contains("platform", StringComparison.Ordinal))
        {
            suggestions.Add("Use ios or android. Available targets are shown by: ansight device list");
        }

        AppendCommandHelpSuggestion(normalized, suggestions);
        return suggestions.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static void AppendCommandHelpSuggestion(string requirement, ICollection<string> suggestions)
    {
        var command = requirement switch
        {
            var value when value.Contains("app action", StringComparison.Ordinal) => "app",
            var value when value.Contains("auth action", StringComparison.Ordinal) => "auth",
            var value when value.Contains("host subcommand", StringComparison.Ordinal) => "host",
            var value when value.Contains("device subcommand", StringComparison.Ordinal) => "device",
            var value when value.Contains("location action", StringComparison.Ordinal) => "device",
            var value when value.Contains("input action", StringComparison.Ordinal) => "input",
            var value when value.Contains("ui action", StringComparison.Ordinal) => "ui",
            var value when value.Contains("keyboard action", StringComparison.Ordinal) => "keyboard",
            var value when value.Contains("pairing action", StringComparison.Ordinal) => "pairing",
            var value when value.Contains(".net profiling action", StringComparison.Ordinal) => "profile dotnet",
            var value when value.Contains("native profiling action", StringComparison.Ordinal) => "profile",
            var value when value.Contains("process sampling action", StringComparison.Ordinal) => "profile sample",
            var value when value.Contains("profiling action", StringComparison.Ordinal) => "profile",
            var value when value.Contains("profiling technology", StringComparison.Ordinal) => "profile",
            var value when value.Contains("repository command", StringComparison.Ordinal) => "repo",
            var value when value.Contains("automation action", StringComparison.Ordinal) => "repo",
            var value when value.Contains("secret action", StringComparison.Ordinal) => "secret",
            var value when value.Contains("session action", StringComparison.Ordinal) => "session",
            var value when value.Contains("test subcommand", StringComparison.Ordinal) => "test",
            var value when value.Contains("workspace action", StringComparison.Ordinal) => "workspace",
            var value when value.Contains("definition kind", StringComparison.Ordinal) => "workspace",
            _ => null
        };

        if (command is not null)
        {
            suggestions.Add($"Show command usage and examples: ansight {command} help");
        }
    }

    private static bool ContainsAny(string source, params string[] values)
        => values.Any(value => source.Contains(value, StringComparison.Ordinal));
}
