namespace Ansight.Cli;

internal static class CliCommandHelp
{
    public static bool IsRequested(CliArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.ShowHelp
               || (arguments.Positionals.Count > 1
                   && string.Equals(
                       arguments.Positionals[1],
                       "help",
                       StringComparison.OrdinalIgnoreCase));
    }

    public static int Write(CliOutput output, string helpText)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.WriteText(helpText);
        return CliExitCodes.Success;
    }
}
