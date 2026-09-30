using Ansight.Host;

namespace Ansight.Cli.Commands.Serve;

internal static class ServeCommand
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

        var isStop = arguments.Positionals.Count > 1
                     && string.Equals(arguments.Positionals[1], "stop", StringComparison.OrdinalIgnoreCase);
        if (isStop)
        {
            throw new CliUsageException(
                "The resident host always serves the local explorer. Stop both with: ansight host stop");
        }

        arguments.EnsurePositionalCount(
            1,
            "ansight serve [--port <port>] [--path <path>] [--open] [--session <session-id>]");
        var options = CliRuntime.ResolveOptions(arguments);
        var urlPath = LocalSettingsStore.ResolveExplorerPath(
            arguments,
            "path",
            options.DataDirectory);
        var port = LocalSettingsStore.ResolveExplorerPort(
            arguments,
            "port",
            options.DataDirectory);
        await using var lease = await CliRuntimeLease.CreateAsync(
            options,
            start: true,
            cancellationToken).ConfigureAwait(false);
        var runtime = lease.Runtime;

        var result = await runtime.SessionReplays.StartExplorerAsync(
                new SessionExplorerStartRequest(
                    port,
                    arguments.GetOption("session"),
                    urlPath),
                cancellationToken)
            .ConfigureAwait(false);
        var isResidentHost = CliCommandContext.Current is not null;
        if (isResidentHost && result.IsSuccess
            && CliRuntime.ReadMetadata(options.DataDirectory)?.ExplorerUrl is { } publicUrl)
        {
            var address = new Uri(publicUrl);
            result = result with { ExplorerUrl = address, Port = address.Port };
        }
        string? openWarning = null;
        if (result.IsSuccess && result.ExplorerUrl is not null && arguments.HasFlag("open"))
        {
            openWarning = CliBrowserLauncher.TryOpen(result.ExplorerUrl);
        }

        output.Write(
            new LocalServeOutput(
                "ansight.local-serve/v1",
                result.IsSuccess,
                result.Message,
                result.ExplorerUrl?.ToString(),
                result.Port,
                result.WasAlreadyRunning,
                isResidentHost),
            () => BuildMessage(result, isResidentHost, openWarning));
        if (!result.IsSuccess)
        {
            return CliExitCodes.Failure;
        }

        if (isResidentHost)
        {
            return CliExitCodes.Success;
        }

        try
        {
            await runtime.SessionReplays.WaitForExplorerStopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await runtime.SessionReplays.StopExplorerAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return CliExitCodes.Success;
    }

    private static string BuildMessage(
        SessionExplorerStartResult result,
        bool isResidentHost,
        string? openWarning)
    {
        if (!result.IsSuccess)
        {
            return result.Message;
        }

        var lifetime = isResidentHost
            ? "The explorer stays active with the resident host. Stop both with: ansight host stop"
            : "Press Ctrl+C to stop the local explorer and host.";
        var warning = string.IsNullOrWhiteSpace(openWarning)
            ? string.Empty
            : $"\nBrowser warning: {openWarning}";
        return $"{result.Message}\nExplorer: {result.ExplorerUrl}\n{lifetime}{warning}";
    }

    private static string BuildHelp()
        => """
           Usage:
             ansight serve [options]

           Starts a loopback-only web explorer backed by the reusable Ansight host. It lists
           recorded and live sessions and includes screenshots, logs, telemetry, touches,
           visual-tree inspection, artifacts, registered-app trends history, manual simulator
           GPS, and GPX/KML route replay.

           Options:
             --session <session-id>   Select this session when the explorer opens
             --port <port>            Explorer HTTP port; 0 chooses an available port
             --path <path>            Use / or a fixed URL-safe segment
             --open                   Open the explorer in the default browser
             --help, -h               Show this help

           Repository triggers:
             --enable-repository-automations  Allow trusted trigger execution (default)
             --disable-repository-automations Disable repository triggers for this host
             --automation-repository <path>   Load a repository at startup; may be repeated

           Trigger modules declare their app ID. The host matches that ID against SDK session
           events when the app connects. Stored app/codebase links with repository automation
           enabled are restored automatically.

           The standalone command also starts the SDK host and runs until Ctrl+C. A resident
           CLI host always serves the explorer until the host is stopped.

           The default local URL is http://127.0.0.1:47231/ansight/. A fixed path, especially
           `/`, is easier to discover and should only be used when that tradeoff is acceptable.
           Change the defaults with `ansight config set explorer-path <path>` and
           `ansight config set explorer-port <port>`, or unset them to restore random path and
           automatic port selection. --path and --port override the configured defaults.
           """;
}
