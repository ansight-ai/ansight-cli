namespace Ansight.Cli.Commands.AppGraph;

internal enum AppGraphStoreMode
{
    Hosted,
    Local
}

internal static class AppGraphStoreOptions
{
    public static AppGraphStoreMode Resolve(CliArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var value = arguments.GetOption("graph-store")?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return arguments.Positionals.FirstOrDefault()?.Equals("cloud", StringComparison.OrdinalIgnoreCase) == true
                ? AppGraphStoreMode.Hosted : AppGraphStoreMode.Local;
        }

        return value.ToLowerInvariant() switch
        {
            "hosted" or "server" or "cloud" => AppGraphStoreMode.Hosted,
            "local" => AppGraphStoreMode.Local,
            _ => throw new CliUsageException("--graph-store must be hosted or local.")
        };
    }
}
