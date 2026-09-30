using Ansight.Host;

namespace Ansight.Cli.Commands.Pairing;

internal static class PairingCommands
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

        var action = arguments.RequirePositional(1, "pairing action").ToLowerInvariant();
        var options = CliRuntime.ResolveOptions(arguments);
        await using var lease = await CliRuntimeLease.CreateAsync(
            options,
            start: false,
            cancellationToken).ConfigureAwait(false);
        return action switch
        {
            "issue" or "create" => Issue(lease.Runtime, arguments, output),
            "list" => List(lease.Runtime, arguments, output),
            "get" => Get(lease.Runtime, arguments, output),
            "revoke" or "remove" or "delete" => Revoke(lease.Runtime, arguments, output),
            _ => throw new CliUsageException(
                $"Unknown pairing action '{action}'. Expected issue, list, get, or revoke.")
        };
    }

    private static int Issue(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        if (arguments.Positionals.Count > 3)
        {
            throw new CliUsageException(
                "Unexpected positional argument. Usage: ansight pairing issue [app-id] [--name <name>] [--duration <duration>] [--code|--qr] [--qr-output <path>] [--host-address <LAN-IP>]");
        }

        var codeRequested = arguments.HasFlag("code");
        var terminalQrRequested = arguments.HasFlag("qr");
        var qrOutputPath = arguments.GetOption("qr-output");
        var qrFileRequested = qrOutputPath is not null;
        var pairingCodeRequested = codeRequested || terminalQrRequested || qrFileRequested;
        if ((terminalQrRequested || qrFileRequested) && !runtime.GetStatusSnapshot().IsRunning)
        {
            throw new CliHostUnavailableException(
                "Start the resident host with 'ansight host run' before issuing a phone enrollment QR.");
        }

        var appId = arguments.Positionals.Count == 3 ? arguments.Positionals[2] : null;
        var result = runtime.Pairing.Issue(
            appId,
            arguments.GetOption("name"),
            arguments.GetOption("duration"));
        PairingCodeResult? codeResult = null;
        PairingQrResult? qrResult = null;
        if (result.IsSuccess && pairingCodeRequested && !qrFileRequested && result.Invite is not null)
        {
            codeResult = runtime.Pairing.CreateCode(
                result.Invite.InviteId,
                arguments.GetOption("host-address"));
            if (!codeResult.IsSuccess)
            {
                runtime.Pairing.Revoke(result.Invite.InviteId);
            }
        }

        if (result.IsSuccess && qrFileRequested && result.Invite is not null)
        {
            qrResult = runtime.Pairing.CreateQr(
                result.Invite.InviteId,
                qrOutputPath!,
                arguments.GetOption("host-address"),
                arguments.HasFlag("overwrite"));
            if (qrResult.IsSuccess)
            {
                codeResult = new PairingCodeResult(
                    true,
                    $"Created one-time pairing code for '{result.Invite.InviteId}'.",
                    result.Invite.InviteId,
                    qrResult.PairingCode,
                    qrResult.HostAddresses);
            }
            else
            {
                runtime.Pairing.Revoke(result.Invite.InviteId);
            }
        }

        output.Write(
            new PairingIssueOutput(
                "ansight.pairing-issue/v4",
                result.IsSuccess,
                result.Message,
                result.Invite,
                result.InviteFilePath,
                result.Duration,
                codeResult,
                qrResult),
            () => BuildIssueMessage(result, codeResult, qrResult, terminalQrRequested));
        return result.IsSuccess
               && codeResult?.IsSuccess != false
               && qrResult?.IsSuccess != false
            ? CliExitCodes.Success
            : CliExitCodes.Failure;
    }

    private static string BuildIssueMessage(
        PairingInviteIssueResult result,
        PairingCodeResult? codeResult,
        PairingQrResult? qrResult,
        bool terminalQrRequested)
    {
        if (!result.IsSuccess)
        {
            return result.Message;
        }

        if (codeResult?.IsSuccess == false)
        {
            return $"{codeResult.Message}\nThe unusable enrollment invite was revoked.";
        }

        if (qrResult?.IsSuccess == false)
        {
            return $"{qrResult.Message}\nThe unusable enrollment invite was revoked.";
        }

        var inviteDetails = $"{result.Message}\nInvite file: {result.InviteFilePath}\nExpires: {result.Invite?.ExpiresAtUtc:O}";
        if (!string.IsNullOrWhiteSpace(codeResult?.PairingCode))
        {
            inviteDetails += $"\nPairing code (one-time credential; keep private):\n{codeResult.PairingCode}";
        }

        if (terminalQrRequested && !string.IsNullOrWhiteSpace(codeResult?.PairingCode))
        {
            inviteDetails += $"\nQR (scan from the Ansight-enabled Debug app):\n{CliQrTerminalRenderer.Render(codeResult.PairingCode)}";
        }

        if (qrResult is not null)
        {
            inviteDetails += $"\nQR file: {qrResult.QrFilePath}";
        }

        return AppendPairingCaptureInstructions(inviteDetails);
    }

    private static string AppendPairingCaptureInstructions(string inviteDetails)
        => $"{inviteDetails}\nKeep the resident host running. Capture starts automatically after the app pairs and opens its live connection.";

    private static int List(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(2, "ansight pairing list [--app-id <app-id>] [--active-only]");
        var activeOnly = arguments.HasFlag("active-only");
        var invites = runtime.Pairing.List(
            arguments.GetOption("app-id"),
            includeConsumed: !activeOnly,
            includeExpired: !activeOnly);
        output.Write(
            new PairingListOutput("ansight.pairing-invites/v1", invites),
            () => invites.Count == 0
                ? "No enrollment invites found."
                : string.Join(
                    Environment.NewLine,
                    invites.Select(invite =>
                        $"{invite.InviteId}\t{invite.Status}\t{invite.AppId}\t{invite.ExpiresAtUtc:O}")));
        return CliExitCodes.Success;
    }

    private static int Get(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(3, "ansight pairing get <invite-id>");
        var inviteId = arguments.RequirePositional(2, "invite identifier");
        var invite = runtime.Pairing.Get(inviteId);
        output.Write(
            new PairingDetailOutput(
                "ansight.pairing-invite/v2",
                inviteId,
                invite is not null,
                invite?.Summary),
            () => invite is null
                ? $"Enrollment invite '{inviteId}' was not found."
                : $"{invite.Summary.InviteId}\t{invite.Summary.Status}\t{invite.Summary.AppId}\t{invite.Summary.ExpiresAtUtc:O}");
        return invite is null ? CliExitCodes.Failure : CliExitCodes.Success;
    }

    private static int Revoke(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(3, "ansight pairing revoke <invite-id>");
        var result = runtime.Pairing.Revoke(arguments.RequirePositional(2, "invite identifier"));
        output.Write(
            new PairingOperationOutput("ansight.pairing-operation/v1", "revoke", result),
            () => result.Message);
        return result.IsSuccess ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    private static string BuildHelp()
        => """
           Issue and manage SDK enrollment invitations

           Usage:
             ansight pairing issue [app-id] [--code|--qr] [options]
             ansight pairing list [--app-id <app-id>] [--active-only]
             ansight pairing get <invite-id>
             ansight pairing revoke <invite-id>

           Commands:
             issue      Create a short-lived enrollment invitation; alias: create
             list       List invitations, optionally filtered by application or active state
             get        Show one invitation without exposing its enrollment secret
             revoke     Revoke an invitation; aliases: remove, delete

           QR options:
             --code                     Print the one-time pairing code for manual entry
             --qr                       Render a scannable QR directly in the terminal
             --qr-output <path>         Also write a PNG QR to an explicit path
             --host-address <LAN-IP>    Address the phone can use to reach this host
             --overwrite                Replace an existing QR output file

           Invitation options:
             --name <name>              Human-readable invitation name
             --duration <duration>      Invitation lifetime, for example 15m or 1h
             --app-id <app-id>          Filter `list` by application
             --active-only              Hide consumed, revoked, and expired invitations

           Phone capture requires a resident host reachable from the phone:
             All in one: `ansight host run --pair <app-id>`

             Or use two terminals:
             1. Run `ansight host run`.
             2. Run `ansight pairing issue <app-id> --qr` in another terminal.
             3. Scan the terminal QR, or enter the printed pairing code manually.

           The issue command returns after displaying the invite. The resident host keeps listening;
           after pairing, it creates a live session and capture starts when the SDK connects.
           `--json` returns the pairing code as structured data and omits terminal QR artwork.

           Pairing codes contain a one-time enrollment credential. They are only returned by
           `issue --code` or `issue --qr`; `list` and `get` never expose them.
           Find app IDs with `ansight app list` and existing invite IDs with `ansight pairing list`.
           """;

}
