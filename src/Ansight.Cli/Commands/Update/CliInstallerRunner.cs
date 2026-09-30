using System.Diagnostics;
using System.Text;

namespace Ansight.Cli.Commands.Update;

internal sealed class CliInstallerRunner
{
    private const string DefaultInstallerBaseUrl = "https://www.ansight.ai";

    private readonly HttpClient httpClient;
    private readonly CliInstallationState installationState;

    public CliInstallerRunner(HttpClient httpClient, CliInstallationState installationState)
    {
        this.httpClient = httpClient;
        this.installationState = installationState;
    }

    public async Task<CliUpdateAppliedOutput> ApplyAsync(
        CliUpdateStatusOutput status,
        IProgress<CliUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!status.IsVersionChange && !status.IsChannelChange)
        {
            return CreateResult(status, wasUpdated: false, channelChanged: false, "The Ansight CLI is already current.");
        }

        if (!status.IsVersionChange
            && status.IsChannelChange
            && installationState.Receipt is not null)
        {
            progress?.Report(new CliUpdateProgress(
                $"Changing the update channel to {status.Channel}..."));
            CliInstallationReceiptStore.Save(
                installationState.ReceiptPath,
                installationState.Receipt with
                {
                    Channel = status.Channel,
                    ReleaseUrl = status.ReleaseUrl,
                    DownloadBaseUrl = status.DownloadBaseUrl,
                    ArchiveUrl = status.ArchiveUrl,
                    InstalledAtUtc = DateTimeOffset.UtcNow
                });
            return CreateResult(
                status,
                wasUpdated: false,
                channelChanged: true,
                $"The Ansight CLI update channel is now {status.Channel}.");
        }

        var installerExtension = OperatingSystem.IsWindows() ? "ps1" : "sh";
        var installerUrl = Environment.GetEnvironmentVariable("ANSIGHT_INSTALLER_URL");
        if (string.IsNullOrWhiteSpace(installerUrl))
        {
            installerUrl = $"{DefaultInstallerBaseUrl}/install.{installerExtension}";
        }

        var installerPath = Path.Combine(
            Path.GetTempPath(),
            $"ansight-update-{Guid.NewGuid():N}.{installerExtension}");
        try
        {
            progress?.Report(new CliUpdateProgress("Downloading the platform installer..."));
            var installer = await httpClient.GetStringAsync(installerUrl, cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(installerPath, installer, cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(new CliUpdateProgress(
                $"Installing Ansight CLI {status.LatestVersion} ({status.LatestBuildNumber})..."));
            var result = await RunInstallerAsync(
                    installerPath,
                    status,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"The Ansight installer exited with code {result.ExitCode}:{Environment.NewLine}{BuildFailureDetail(result)}");
            }

            return CreateResult(
                status,
                wasUpdated: true,
                channelChanged: status.IsChannelChange,
                $"Updated Ansight CLI to {status.LatestVersion} ({status.LatestBuildNumber}) on the {status.Channel} channel. Restart any running Ansight host to use the new build.");
        }
        finally
        {
            if (File.Exists(installerPath))
            {
                File.Delete(installerPath);
            }
        }
    }

    private async Task<CliInstallerProcessResult> RunInstallerAsync(
        string installerPath,
        CliUpdateStatusOutput status,
        IProgress<CliUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/bash",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(installerPath);
            startInfo.ArgumentList.Add("-Version");
            startInfo.ArgumentList.Add(status.LatestVersion);
            startInfo.ArgumentList.Add("-BuildNumber");
            startInfo.ArgumentList.Add(status.LatestBuildNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-Channel");
            startInfo.ArgumentList.Add(status.Channel);
            startInfo.ArgumentList.Add("-NoSetup");
        }
        else
        {
            startInfo.ArgumentList.Add(installerPath);
            startInfo.ArgumentList.Add("--version");
            startInfo.ArgumentList.Add(status.LatestVersion);
            startInfo.ArgumentList.Add("--build-number");
            startInfo.ArgumentList.Add(status.LatestBuildNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--channel");
            startInfo.ArgumentList.Add(status.Channel);
            startInfo.ArgumentList.Add("--no-setup");
        }

        startInfo.Environment["ANSIGHT_INSTALL_VERSION"] = status.LatestVersion;
        startInfo.Environment["ANSIGHT_INSTALL_BUILD_NUMBER"] = status.LatestBuildNumber.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["ANSIGHT_INSTALL_CHANNEL"] = status.Channel;
        startInfo.Environment["ANSIGHT_RELEASE_URL"] = status.ReleaseUrl;
        startInfo.Environment["ANSIGHT_DOWNLOAD_BASE_URL"] = status.DownloadBaseUrl;
        if (!string.IsNullOrWhiteSpace(installationState.Receipt?.InstallRoot))
        {
            startInfo.Environment["ANSIGHT_INSTALL_ROOT"] = installationState.Receipt.InstallRoot;
        }

        if (!string.IsNullOrWhiteSpace(installationState.Receipt?.BinDirectory))
        {
            startInfo.Environment["ANSIGHT_BIN_DIR"] = installationState.Receipt.BinDirectory;
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("The Ansight installer process could not be started.");
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var standardOutputPump = PumpInstallerOutputAsync(
            process.StandardOutput,
            standardOutput,
            progress,
            cancellationToken);
        var standardErrorPump = PumpInstallerOutputAsync(
            process.StandardError,
            standardError,
            progress,
            cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(standardOutputPump, standardErrorPump).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TerminateInstallerAsync(process).ConfigureAwait(false);
            await ObserveCancelledPumpsAsync(standardOutputPump, standardErrorPump).ConfigureAwait(false);
            throw;
        }

        return new CliInstallerProcessResult(
            process.ExitCode,
            standardOutput.ToString(),
            standardError.ToString());
    }

    internal static async Task PumpInstallerOutputAsync(
        TextReader reader,
        StringBuilder capturedOutput,
        IProgress<CliUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            var chunk = new string(buffer, 0, read);
            capturedOutput.Append(chunk);
            progress?.Report(new CliUpdateProgress(chunk, IsRawInstallerOutput: true));
        }
    }

    internal static string BuildFailureDetail(CliInstallerProcessResult result)
    {
        var detail = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            detail.Append("stdout: ");
            detail.Append(result.StandardOutput.Trim());
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            if (detail.Length > 0)
            {
                detail.AppendLine();
            }

            detail.Append("stderr: ");
            detail.Append(result.StandardError.Trim());
        }

        return detail.Length == 0
            ? "The installer did not produce diagnostic output."
            : detail.ToString();
    }

    private static async Task ObserveCancelledPumpsAsync(params Task[] pumps)
    {
        try
        {
            await Task.WhenAll(pumps).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async Task TerminateInstallerAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and termination request.
        }
    }

    private static CliUpdateAppliedOutput CreateResult(
        CliUpdateStatusOutput status,
        bool wasUpdated,
        bool channelChanged,
        string message)
        => new(
            "ansight.cli.update-applied/v1",
            status.CurrentVersion,
            status.CurrentBuildNumber,
            status.LatestVersion,
            status.LatestBuildNumber,
            status.Channel,
            wasUpdated,
            channelChanged,
            message);
}

internal sealed record CliInstallerProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);
