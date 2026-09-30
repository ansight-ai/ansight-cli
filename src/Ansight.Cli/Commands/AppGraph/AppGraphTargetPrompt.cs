using System.Text.Json;

namespace Ansight.Cli.Commands.AppGraph;

internal static class AppGraphTargetPrompt
{
    public static async Task<CliArguments> PrepareAsync(
        CliArguments arguments,
        CliOutput output,
        TextReader? standardInput,
        bool standardInputIsInteractive,
        CancellationToken cancellationToken,
        Func<CliArguments, string, string?, CancellationToken, Task<bool?>>? liveSessionProbe = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        if (!IsAppGraphExploration(arguments)
            || CliCommandHelp.IsRequested(arguments)
            || arguments.Positionals.Count > 3)
        {
            return arguments;
        }

        var positionalAppId = arguments.Positionals.Count == 3
            ? arguments.Positionals[2]
            : null;
        var optionAppId = arguments.GetOption("app-id");
        if (positionalAppId is not null && optionAppId is not null)
        {
            return arguments;
        }

        var configuredWorkspace = arguments.HasFlag("workspace")
            ? arguments.RequireOption("workspace")
            : Environment.CurrentDirectory;
        if (string.IsNullOrWhiteSpace(configuredWorkspace))
        {
            throw new CliUsageException("--workspace requires a non-empty folder path.");
        }

        var workspacePath = Path.GetFullPath(configuredWorkspace);
        IReadOnlyList<AppGraphWorkspaceApp> candidates = [];
        var appId = positionalAppId ?? optionAppId;
        string? appName = null;
        if (string.IsNullOrWhiteSpace(appId))
        {
            candidates = FindWorkspaceApps(
                CliRuntime.ResolveOptions(arguments).DataDirectory,
                workspacePath);
            if (candidates.Count == 1)
            {
                appId = candidates[0].AppId;
                appName = candidates[0].Name;
            }
        }
        if (appId is null
            && !arguments.IsJson
            && !arguments.IsSilent
            && standardInputIsInteractive
            && standardInput is not null)
        {
            if (candidates.Count == 0)
            {
                appId = PromptForAppId(output, standardInput);
            }
            else
            {
                var selectedApp = PromptForWorkspaceApp(candidates, output, standardInput);
                appId = selectedApp.AppId;
                appName = selectedApp.Name;
            }
        }

        if (string.IsNullOrWhiteSpace(appId))
        {
            return arguments;
        }

        var normalizedAppId = appId.Trim();
        var enrichedArguments = arguments;
        if (positionalAppId is null && optionAppId is null)
        {
            var values = arguments.OriginalArguments.ToList();
            values.Add("--app-id");
            values.Add(normalizedAppId);
            if (arguments.GetOption("workspace") is null)
            {
                values.Add("--workspace");
                values.Add(workspacePath);
            }

            enrichedArguments = CliArguments.Parse(values);
        }

        if (enrichedArguments.HasFlag("launch")
            || enrichedArguments.HasFlag("no-launch")
            || enrichedArguments.IsJson
            || enrichedArguments.IsSilent
            || !standardInputIsInteractive
            || standardInput is null)
        {
            return enrichedArguments;
        }

        liveSessionProbe ??= ControlClient.TryHasConnectedAppSessionAsync;
        var hasLiveSession = await liveSessionProbe(
            enrichedArguments,
            normalizedAppId,
            enrichedArguments.GetOption("session-id"),
            cancellationToken).ConfigureAwait(false);
        if (hasLiveSession is not false
            || !PromptToLaunchApp(appName ?? normalizedAppId, normalizedAppId, output, standardInput))
        {
            return enrichedArguments;
        }

        var launchArguments = enrichedArguments.OriginalArguments.ToList();
        launchArguments.Add("--launch");
        return CliArguments.Parse(launchArguments);
    }

    internal static IReadOnlyList<AppGraphWorkspaceApp> FindWorkspaceApps(
        string dataDirectory,
        string workspacePath)
    {
        var storePath = Path.Combine(dataDirectory, "data", "known-apps.json");
        if (!File.Exists(storePath)) return [];

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(storePath));
            if (!document.RootElement.TryGetProperty("apps", out var apps)
                || apps.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var normalizedWorkspacePath = NormalizeDirectoryPath(workspacePath);
            return apps.EnumerateArray()
                .Select(ReadWorkspaceApp)
                .Where(static app => app is not null)
                .Cast<AppGraphWorkspaceApp>()
                .Where(app => IsPathUnderOrEqual(app.CodebasePath, normalizedWorkspacePath))
                .OrderByDescending(static app => app.CodebasePath.Length)
                .ThenBy(static app => app.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? PromptForAppId(CliOutput output, TextReader standardInput)
    {
        output.WritePrompt("App ID to explore: ");
        return standardInput.ReadLine()?.Trim();
    }

    private static AppGraphWorkspaceApp PromptForWorkspaceApp(
        IReadOnlyList<AppGraphWorkspaceApp> candidates,
        CliOutput output,
        TextReader standardInput)
    {
        output.WritePrompt("Select the workspace app to explore:" + Environment.NewLine);
        for (var index = 0; index < candidates.Count; index++)
        {
            output.WritePrompt($"  {index + 1}. {candidates[index].Name} ({candidates[index].AppId}){Environment.NewLine}");
        }

        output.WritePrompt($"Choice [1-{candidates.Count}]: ");
        var selection = standardInput.ReadLine()?.Trim();
        if (!int.TryParse(selection, out var selectedIndex)
            || selectedIndex < 1
            || selectedIndex > candidates.Count)
        {
            throw new CliUsageException("Select one of the listed workspace apps or pass --app-id <id>.");
        }

        return candidates[selectedIndex - 1];
    }

    private static bool PromptToLaunchApp(
        string appName,
        string appId,
        CliOutput output,
        TextReader standardInput)
    {
        var displayName = appName.Equals(appId, StringComparison.OrdinalIgnoreCase)
            ? $"'{appId}'"
            : $"'{appName}' ({appId})";
        output.WritePrompt(
            $"No live Ansight session is connected for {displayName}. Launch it now? [Y/n] ");
        var response = standardInput.ReadLine();
        if (response is null)
        {
            return false;
        }

        return response.Trim().ToLowerInvariant() switch
        {
            "" or "y" or "yes" => true,
            "n" or "no" => false,
            _ => throw new CliUsageException("Answer yes or no, or pass --launch or --no-launch.")
        };
    }

    private static AppGraphWorkspaceApp? ReadWorkspaceApp(JsonElement element)
    {
        var appId = ReadString(element, "appId");
        var codebasePath = ReadString(element, "codebasePath");
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(codebasePath)) return null;
        return new AppGraphWorkspaceApp(
            appId.Trim(),
            ReadString(element, "name")?.Trim() is { Length: > 0 } name ? name : appId.Trim(),
            NormalizeDirectoryPath(codebasePath));
    }

    private static string? ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsPathUnderOrEqual(string parentPath, string candidatePath)
    {
        var relative = Path.GetRelativePath(parentPath, candidatePath);
        return relative == "."
               || (!relative.Equals("..", StringComparison.Ordinal)
                   && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                   && !Path.IsPathFullyQualified(relative));
    }

    private static string NormalizeDirectoryPath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    private static bool IsAppGraphExploration(CliArguments arguments)
        => arguments.Positionals.Count >= 2
           && arguments.Positionals[0].ToLowerInvariant() is "app-graph" or "app-graphs"
           && arguments.Positionals[1].Equals("explore", StringComparison.OrdinalIgnoreCase);
}

internal sealed record AppGraphWorkspaceApp(string AppId, string Name, string CodebasePath);
