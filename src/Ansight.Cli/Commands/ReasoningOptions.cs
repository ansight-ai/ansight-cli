namespace Ansight.Cli.Commands;

internal static class ReasoningOptions
{
    public static void ValidateForCommand(CliArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var command = arguments.Positionals.ElementAtOrDefault(0)?.ToLowerInvariant();
        var action = arguments.Positionals.ElementAtOrDefault(1)?.ToLowerInvariant();
        if (command == "replay"
            || command == "app" && action == "execute"
            || command is "test" or "tests" && action is "run" or "run-all" or "run-inline"
            || command == "app-graph" && action is "run" or "explore")
        {
            _ = Resolve(arguments);
            _ = ResolveModelOverride(arguments);
        }
    }

    public static string Resolve(CliArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!arguments.HasFlag("reasoning"))
        {
            return AgentReasoningModes.Fast;
        }

        if (arguments.HasFlag("model"))
        {
            throw new CliUsageException("Use --reasoning or the diagnostic --model override, not both.");
        }

        var value = arguments.GetOption("reasoning");
        if (!string.IsNullOrWhiteSpace(value))
        {
            try
            {
                return AgentReasoningModes.Normalize(value);
            }
            catch (ArgumentException)
            {
                // Report provider-independent CLI vocabulary for invalid input.
            }
        }

        throw new CliUsageException("--reasoning must be fast, balanced, or deep.");
    }

    public static string ResolveModelOverride(CliArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!arguments.HasFlag("model"))
        {
            return string.Empty;
        }

        var model = arguments.GetOption("model");
        return !string.IsNullOrWhiteSpace(model)
            ? model.Trim()
            : throw new CliUsageException("--model requires a value.");
    }
}
