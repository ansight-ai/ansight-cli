namespace Ansight.Cli.Commands.Update;

internal static class UpdateCommands
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

        var action = arguments.Positionals.Count == 1
            ? "apply"
            : arguments.Positionals[1].ToLowerInvariant();
        if (action is not ("apply" or "check"))
        {
            throw new CliUsageException(
                $"Unknown update command '{arguments.Positionals[1]}'. Run 'ansight update help' for usage.");
        }

        arguments.EnsurePositionalCount(
            action == "apply" && arguments.Positionals.Count == 1 ? 1 : 2,
            action == "check" ? "ansight update check" : "ansight update");
        var requestedVersion = arguments.GetOption("version");
        var requestedBuildNumber = ReadBuildNumber(arguments.GetOption("build-number"));
        var requestedChannel = arguments.GetOption("channel");
        output.WriteProgress(requestedVersion is null
            ? "[update] Checking for updates..."
            : "[update] Resolving the requested CLI build...");
        var installationState = CliInstallationReceiptStore.Load();
        using var httpClient = CreateHttpClient();
        var service = new CliUpdateService(
            httpClient,
            CliReleaseIdentity.Current,
            installationState,
            CliRuntimeIdentifier.Current);
        var status = await service.CheckAsync(
                requestedChannel,
                requestedVersion,
                requestedBuildNumber,
                cancellationToken)
            .ConfigureAwait(false);
        if (action == "check")
        {
            output.Write(status, () => RenderStatus(status));
            return CliExitCodes.Success;
        }

        if (status.IsVersionChange)
        {
            output.WriteProgress(
                $"[update] Selected Ansight CLI {status.LatestVersion} ({status.LatestBuildNumber}); current is {status.CurrentVersion} ({status.CurrentBuildNumber}).");
        }

        var runner = new CliInstallerRunner(httpClient, installationState);
        var result = await runner.ApplyAsync(
                status,
                CreateProgress(output),
                cancellationToken)
            .ConfigureAwait(false);
        output.Write(result, () => result.Message);
        return CliExitCodes.Success;
    }

    internal static IProgress<CliUpdateProgress> CreateProgress(CliOutput output)
        => new CliProgress<CliUpdateProgress>(value =>
        {
            if (value.IsRawInstallerOutput)
            {
                output.WriteProgressRaw(value.Message);
            }
            else
            {
                output.WriteProgress($"[update] {value.Message}");
            }
        });

    internal static HttpClient CreateHttpClient()
        => new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

    internal static string RenderStatus(CliUpdateStatusOutput status)
    {
        if (status.IsUpdateAvailable)
        {
            var summary = string.IsNullOrWhiteSpace(status.Summary)
                ? string.Empty
                : $"{Environment.NewLine}{status.Summary}";
            return $"Ansight CLI {status.LatestVersion} ({status.LatestBuildNumber}) is available on the {status.Channel} channel."
                   + $"{Environment.NewLine}Current: {status.CurrentVersion} ({status.CurrentBuildNumber})"
                   + $"{Environment.NewLine}Run 'ansight update' to install it.{summary}";
        }

        if (status.IsVersionChange)
        {
            return $"Selected Ansight CLI build: {status.LatestVersion} ({status.LatestBuildNumber})."
                   + $"{Environment.NewLine}Current: {status.CurrentVersion} ({status.CurrentBuildNumber})";
        }

        if (status.IsChannelChange)
        {
            return $"The installed build is current. Run 'ansight update --channel {status.Channel}' to change update channels.";
        }

        return $"Ansight CLI {status.CurrentVersion} ({status.CurrentBuildNumber}) is current on the {status.Channel} channel.";
    }

    private static long? ReadBuildNumber(string? source)
    {
        if (source is null)
        {
            return null;
        }

        if (!long.TryParse(
                source,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var buildNumber)
            || buildNumber <= 0)
        {
            throw new CliUsageException("--build-number must be a positive integer.");
        }

        return buildNumber;
    }

    private static string BuildHelp()
        => """
           Check, install, pin, or switch Ansight CLI releases.

           Usage:
             ansight update check [--channel public|preview] [--json]
             ansight update [--channel public|preview] [--json]
             ansight update --version <version> --build-number <YYYYMMDDNN> [--channel public|preview]

           With no version selection, update uses the latest build from the installed
           channel. Exact build selection requires both the human version string and
           the daily integer build number. Archive SHA-256 validation is performed by
           the platform installer before the active CLI is changed.
           """;
}
