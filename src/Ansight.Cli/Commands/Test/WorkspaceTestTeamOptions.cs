using Ansight.Host;

namespace Ansight.Cli.Commands.Test;

internal static class WorkspaceTestTeamOptions
{
    public static Guid? Resolve(CliArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var value = arguments.GetOption("team-id")
                    ?? Environment.GetEnvironmentVariable("ANSIGHT_TEAM_ID");
        if (value is null)
        {
            return null;
        }

        if (!Guid.TryParse(value, out var teamId) || teamId == Guid.Empty)
        {
            throw new CliUsageException(
                $"--team-id or ANSIGHT_TEAM_ID must be a non-empty GUID.");
        }

        return teamId;
    }
}
