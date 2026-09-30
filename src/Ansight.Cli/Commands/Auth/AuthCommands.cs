namespace Ansight.Cli.Commands.Auth;

internal static class AuthCommands
{
    public static Task<int> RunAsync(CliArguments arguments, CliOutput output, CancellationToken cancellationToken)
    {
        if (CliCommandHelp.IsRequested(arguments) || arguments.Positionals.Count == 1)
            return Task.FromResult(CliCommandHelp.Write(output, BuildHelp()));

        return Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<Task<int>>("AuthCommands.RunAsync", [arguments, output, cancellationToken, null, null, null]);
    }
    private static string BuildHelp()
        => """
           Authenticate the headless CLI with an Ansight account

           Usage:
             ansight auth login [options]
             ansight auth login --provider <name> [options]
             ansight auth login --email <address> [--otp] [secret-input]
             ansight auth login --access-token [secret-input]
             ansight auth login --device [options]
             ansight auth refresh
             ansight auth status
             ansight auth logout [--local]

           Commands:
             login      Sign in and save the resulting account session; aliases: sign-in, signin
             refresh    Refresh the current account session
             status     Show identity, authentication method, and token expiry—not token values
             logout     Revoke the remote session and clear local credentials; aliases: sign-out, signout

           Login modes:
             (no mode)             Open app.ansight.ai, sign in, and return to this CLI
             --provider <name>      Browser authorization-code login with PKCE
             --email <address>      Email/password login with a hidden password prompt
             --email <address> --otp
                                   Request and verify an email one-time code
             --access-token         Validate an account access token supplied as secret input
             --device               OAuth RFC 8628 device flow; provider endpoints are required

           Options:
             --no-browser           Print the PKCE URL instead of opening it
             --callback-port <port> Fixed local browser-login callback port
             --timeout <seconds>    Browser-login timeout
             --portal-url <url>     Override the account portal URL
             --local                Logout locally without remote revocation
           Secret input:
             --stdin                Read the password, OTP, or token from standard input
             --from-env <NAME>      Read it from a named environment variable
             With neither option, an interactive hidden prompt is used. Secret values are never
             accepted as command-line arguments or printed in command output.
           """;
}
