using Ansight.Host;

namespace Ansight.Cli.Commands.Companion;

internal static class CompanionCommands
{
    public static Task<int> RunAsync(CliArguments arguments, CliOutput output, CancellationToken cancellationToken)
    {
        if (CliCommandHelp.IsRequested(arguments) || arguments.Positionals.Count == 1)
            return Task.FromResult(CliCommandHelp.Write(output, BuildHelp()));

        return Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<Task<int>>("CompanionCommands.RunAsync", [arguments, output, cancellationToken]);
    }

    private static string BuildHelp()
        => """
           Manage the remote-control companion app that controls and annotates simulators.

           Usage:
             ansight companion access status
             ansight companion access enable [--mode session|always]
             ansight companion access disable
             ansight companion machines list [--include-revoked]
             ansight companion machines get <machine-id>
             ansight companion machines rename <machine-id> <display-name>
             ansight companion machines revoke <machine-id>
             ansight companion machines restore <machine-id>
             ansight companion machines delete <machine-id> --force
             ansight companion connections list
             ansight companion connections disconnect <session-id>
             ansight companion connections disconnect-all

           Aliases:
             remote, remote-simulator

           Requirements:
             Start a CLI companion host with:
               ansight host run --companion-access session --machine-name "Development Mac"
             Add --team-id <uuid> when more than one eligible organisation is enabled.
             Use the same --data-dir on management commands when the host uses a custom one.
             Access and connection commands target the resident CLI companion host.
             Machine commands use the account established by `ansight auth login`.

           Notes:
             These are companion-app machine registrations and live remote sessions.
             SDK enrollment invites remain under `ansight pairing`.
           """;
}
