using Ansight.Host.Cloud.Accounts;
using Ansight.Host;
using System.Globalization;

namespace Ansight.Cli.Commands.Account;

internal static class AccountCommands
{
    public static Task<int> RunAsync(CliArguments arguments, CliOutput output, CancellationToken cancellationToken)
    {
        if (CliCommandHelp.IsRequested(arguments))
            return Task.FromResult(CliCommandHelp.Write(output, BuildHelp()));

        return Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<Task<int>>("AccountCommands.RunAsync", [arguments, output, cancellationToken]);
    }

    internal static int OpenPortal(CliArguments arguments, CliOutput output, Func<Uri, string?> openBrowser)
        => Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<int>("AccountCommands.OpenPortal", [arguments, output, openBrowser]);
    private static string BuildHelp()
        => """
           Open the Ansight web account portal, inspect cloud grants, or manage CLI authentication.

           Usage:
             ansight account
             ansight account open
             ansight account access
             ansight account grants [--team-id <uuid>]
             ansight account usage [--team-id <uuid>]
             ansight account machines list --team-id <uuid>
             ansight account machines remove <machine-id> --team-id <uuid>
             ansight account login [options]
             ansight account refresh
             ansight account status
             ansight account logout [--local]

           `account` and `account open` launch https://app.ansight.ai/ in the default browser with CLI signup attribution.
           `account access` checks the current cloud grant against the server and reports its expiry.
           `account grants` reports plan, effective access status, expiry, retention, limits, and capabilities for one or all accessible organisations.
           `account usage` (alias: `account budget`) reports the operational token bucket and the active OpenAI billing allowance separately.
           `account machines` lets organisation owners and administrators list or globally remove registered CLI machines.
           Removing a machine also removes its companion authorization and requires that CLI to sign in again.
           When more than one eligible organisation is available, pass --team-id to select the organisation explicitly.
           Authentication actions remain aliases for the matching `ansight auth` commands.
           `signin` and `sign-in` alias `login`; `signout` and `sign-out` alias `logout`.
           Use --json for versioned machine-readable output.
           """;
}
