namespace Ansight.Cli.Commands.Workspace;

internal static class WorkspaceRegistrationPrompt
{
    public static CliArguments Prepare(
        CliArguments arguments,
        CliOutput output,
        TextReader? standardInput,
        bool standardInputIsInteractive)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        if (!IsWorkspaceInitialization(arguments)
            || CliCommandHelp.IsRequested(arguments)
            || arguments.GetOption("app-id") is not null
            || arguments.HasFlag("no-register")
            || arguments.IsJson
            || arguments.IsSilent
            || !standardInputIsInteractive
            || standardInput is null)
        {
            return arguments;
        }

        output.WritePrompt(
            "App ID to register for automatic session analysis (leave blank to skip): ");
        var appId = standardInput.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(appId))
        {
            return arguments;
        }

        var enrichedArguments = arguments.OriginalArguments.ToList();
        enrichedArguments.Add("--app-id");
        enrichedArguments.Add(appId);
        return CliArguments.Parse(enrichedArguments);
    }

    private static bool IsWorkspaceInitialization(CliArguments arguments)
        => arguments.Positionals.Count >= 2
           && arguments.Positionals[0].ToLowerInvariant() is "workspace" or "workspaces"
           && arguments.Positionals[1].ToLowerInvariant() is "init" or "initialize";
}
