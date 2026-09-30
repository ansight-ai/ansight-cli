using Ansight.Host;

namespace Ansight.Cli.Commands.Secret;

internal static class SecretCommands
{
    public static async Task<int> RunAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        if (CliCommandHelp.IsRequested(arguments))
        {
            return CliCommandHelp.Write(output, BuildHelp());
        }

        var action = arguments.RequirePositional(1, "secret action").ToLowerInvariant();
        var options = CliRuntime.ResolveOptions(arguments);
        await using var lease = await CliRuntimeLease.CreateAsync(
            options,
            start: false,
            cancellationToken).ConfigureAwait(false);
        var runtime = lease.Runtime;
        return action switch
        {
            "list" => List(runtime, arguments, output),
            "set" => await SetAsync(runtime, arguments, output, cancellationToken),
            "remove" or "delete" => Remove(runtime, arguments, output),
            _ => throw new CliUsageException(
                $"Unknown secret action '{action}'. Expected list, set, or remove.")
        };
    }

    private static int List(RuntimeCoordinator runtime, CliArguments arguments, CliOutput output)
    {
        var appId = arguments.RequirePositional(2, "app identifier");
        var secrets = runtime.SimulatorAgent.ListTestSecrets(appId);
        output.Write(
            new SecretListOutput("ansight.secrets/v1", appId, secrets),
            () => secrets.Count == 0
                ? $"No test secrets configured for '{appId}'."
                : string.Join(
                    Environment.NewLine,
                    secrets.Select(secret =>
                        $"{secret.Alias}\t{secret.VersionId}\t{secret.UpdatedUtc:O}")));
        return CliExitCodes.Success;
    }

    private static async Task<int> SetAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(4, "ansight secret set <app-id> <alias> [--stdin|--from-env <NAME>]");
        var appId = arguments.RequirePositional(2, "app identifier");
        var alias = arguments.RequirePositional(3, "secret alias");
        var value = await SecretInput.ReadAsync(arguments, $"Value for {appId}/{alias}: ", cancellationToken)
            .ConfigureAwait(false);
        var metadata = runtime.SimulatorAgent.SetTestSecret(appId, alias, value);
        output.Write(
            new SecretMutationOutput("ansight.secret-mutation/v1", "set", true, metadata),
            () => $"Saved test secret '{metadata.Alias}' for '{metadata.AppId}'.");
        return CliExitCodes.Success;
    }

    private static int Remove(RuntimeCoordinator runtime, CliArguments arguments, CliOutput output)
    {
        var appId = arguments.RequirePositional(2, "app identifier");
        var alias = arguments.RequirePositional(3, "secret alias");
        var removed = runtime.SimulatorAgent.RemoveTestSecret(appId, alias);
        output.Write(
            new SecretMutationOutput("ansight.secret-mutation/v1", "remove", removed, null),
            () => removed
                ? $"Removed test secret '{alias}' for '{appId}'."
                : $"Test secret '{alias}' was not configured for '{appId}'.");
        return removed ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    private static string BuildHelp()
        => """
           Manage test secrets

           Usage:
             ansight secret list <app-id>
             ansight secret set <app-id> <alias> [secret-input]
             ansight secret remove <app-id> <alias>

           Commands:
             list            List aliases, versions, and update times without secret values
             set             Create or replace an app-scoped test secret
             remove          Delete an app-scoped test secret; alias: delete

           Secret input:
             --stdin                Read the value from standard input
             --from-env <NAME>      Read the value from a named environment variable
             With neither option, an interactive hidden prompt is used. Values are never accepted
             as command-line arguments and are never returned by `list`.

           Storage options:
             --secret-store-file <path>  Use an encrypted file instead of the OS vault
             --secret-key-file <path>    Read that file store's AES key from an owner-only file

           By default macOS uses Keychain, Linux uses Secret Service when available, and Windows
           protects the encrypted file key with DPAPI. Run `ansight doctor` to inspect the provider.

           Find app IDs with `ansight app list`. Find existing aliases with
           `ansight secret list <app-id>`; secret values are never displayed.
           """;

}
