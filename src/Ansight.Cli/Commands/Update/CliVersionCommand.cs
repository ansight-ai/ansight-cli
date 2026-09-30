namespace Ansight.Cli.Commands.Update;

internal static class CliVersionCommand
{
    public static Task<int> RunAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CliCommandHelp.IsRequested(arguments))
        {
            return Task.FromResult(CliCommandHelp.Write(output, BuildHelp()));
        }

        if (arguments.Positionals.Count > 0)
        {
            arguments.EnsurePositionalCount(1, "ansight version");
        }

        var identity = CliReleaseIdentity.Current;
        var installationState = CliInstallationReceiptStore.Load();
        var receipt = installationState.Receipt;
        var channel = receipt?.Channel ?? "development";
        var result = new CliVersionOutput(
            "ansight.cli.version/v1",
            identity.Version,
            identity.BuildNumber,
            identity.InformationalVersion,
            identity.CommitSha,
            channel,
            receipt?.Rid ?? CliRuntimeIdentifier.Current,
            CliRuntime.ResolveDataDirectory(arguments.GetOption("data-dir")),
            Environment.ProcessPath ?? "unavailable",
            installationState.IsInstallerManaged,
            installationState.IsInstallerManaged ? installationState.ReceiptPath : null,
            receipt?.ReleaseUrl);
        output.Write(result, () => Render(result));
        return Task.FromResult(CliExitCodes.Success);
    }

    private static string Render(CliVersionOutput result)
    {
        var lines = new List<string>
        {
            $"Ansight CLI {result.Version} ({result.BuildNumber})",
            $"Channel: {result.Channel}",
            $"Commit: {result.CommitSha}",
            $"Runtime: {result.Rid}",
            $"Data: {result.DataDirectory}",
            $"Executable: {result.ExecutablePath}"
        };
        if (!result.IsInstallerManaged)
        {
            lines.Add("Installation: unmanaged development build");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildHelp()
        => """
           Show the immutable Ansight CLI build identity, installed update channel,
           active data directory, and executable path.

           Usage:
             ansight version [--json]
             ansight info [--json]
             ansight --version [--json]

           The release identity contains both the human version string and the
           YYYYMMDDNN integer build number. The channel comes from the installer
           receipt because public releases promote the exact preview binary.
           """;
}
