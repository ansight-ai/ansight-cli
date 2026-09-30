using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Ansight.Host;

namespace Ansight.Cli.Commands.Host;

internal static class HostCommands
{
    private const string DesktopRestartArgumentsEnvironmentVariable = "ANSIGHT_DESKTOP_RESTART_ARGUMENTS";

    public static Task<int> RunAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        if (CliCommandHelp.IsRequested(arguments))
        {
            return Task.FromResult(CliCommandHelp.Write(output, BuildHelp()));
        }

        var subcommand = arguments.RequirePositional(1, "host subcommand").ToLowerInvariant();
        return subcommand switch
        {
            "run" => RunRecoverableHostAsync(arguments, output, cancellationToken,
                CliLocalAccessAuthorizer.Instance, TimeProvider.System),
            "status" => Task.FromResult(ShowStatus(arguments, output)),
            "logs" or "log" => Task.FromResult(ShowLogs(arguments, output)),
            "stop" => StopHostAsync(arguments, output, cancellationToken),
            "desktop-restart" => RunDesktopRestartAsync(arguments, output, cancellationToken),
            _ => throw new CliUsageException(
                $"Unknown host subcommand '{subcommand}'. Expected run, status, logs, or stop.")
        };
    }

    internal static async Task<int> RunRecoverableHostAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken,
        ICliAccessAuthorizer authorizer,
        TimeProvider timeProvider)
    {
        authorizer = CliLocalAccessAuthorizer.Instance;
        var pairRequested = arguments.HasFlag("pair");
        var pairAppId = arguments.GetOption("pair");
        if (pairRequested && string.IsNullOrWhiteSpace(pairAppId))
        {
            throw new CliUsageException(
                CliRequirementGuidance.Append(
                    "--pair requires an app identifier.",
                    "app identifier"));
        }

        var noServe = arguments.HasFlag("no-serve");
        if (noServe && (arguments.HasFlag("open")
                        || arguments.HasFlag("serve-port")
                        || arguments.HasFlag("serve-path")))
        {
            throw new CliUsageException(
                "--no-serve cannot be combined with --open, --serve-port, or --serve-path.");
        }

        var requestedCompanionMode = ParseCompanionAccessMode(arguments);

        var options = CliRuntime.ResolveOptions(arguments);
        if (arguments.HasFlag("runner")) RunnerCommands.EnsureRegistered(options);
        var servePath = LocalSettingsStore.ResolveExplorerPath(
            arguments,
            "serve-path",
            options.DataDirectory);
        var servePort = LocalSettingsStore.ResolveExplorerPort(
            arguments,
            "serve-port",
            options.DataDirectory);
        try { servePath = ExplorerServer.NormalizeRequestedPath(servePath); }
        catch (InvalidOperationException exception) { throw new CliUsageException(exception.Message); }
        using var dataLock = CliDataDirectoryLock.Acquire(options.DataDirectory);
        using var hostShutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var access = new CliLocalHostAccess();
        await using var server = noServe ? null : new CliLocalHostServer(servePort, servePath, access,
            async (returnUrl, token) =>
            {
                // Preserve this host's explicit credential-store configuration.
                var loginOptions = new List<string> { "auth", "login", "--data-dir", options.DataDirectory };
                loginOptions.AddRange(["--return-url", returnUrl.AbsoluteUri]);
                foreach (var option in new[] { "secret-store-file", "secret-key-file" })
                    if (arguments.GetOption(option) is { } value) loginOptions.AddRange(["--" + option, value]);
                var loginArguments = CliArguments.Parse(loginOptions.ToArray());
                var code = await AuthCommands.RunAsync(loginArguments, output, token).ConfigureAwait(false);
                if (code != CliExitCodes.Success) throw new InvalidOperationException("Sign-in failed.");
            }, hostShutdown.Token);
        var updateAvailable = await CliUpdateSuggestion.IsUpdateAvailableAsync(hostShutdown.Token).ConfigureAwait(false);
        await using var desktopHost = await CliDesktopHost.TryStartAsync(server?.Url, updateAvailable,
            output.WriteProgress, hostShutdown.Token).ConfigureAwait(false);
        var accessNotifications = new CliHostAccessNotifications(desktopHost);
        var desktopActions = RunDesktopActionsAsync(desktopHost, arguments, output, hostShutdown);
        if (arguments.HasFlag("open") && server is not null)
        {
            var warning = CliBrowserLauncher.TryOpen(server.Url);
            if (warning is not null) output.WriteProgress(warning);
        }

        try
        {
            string? lastFailureReason = null;
            while (!hostShutdown.IsCancellationRequested)
            {
                access.BeginCheck();
                AccessDecision failure;
                try
                {
                    await using var lease = await CliAccessLease.CreateAsync(authorizer, hostShutdown.Token, timeProvider)
                        .ConfigureAwait(false);
                    using var accessContext = CliAccessContext.Push(authorizer, lease);
                    lastFailureReason = null;
                    try
                    {
                        await RunHostGenerationAsync(arguments, output, lease.Token, options, noServe, pairAppId,
                            requestedCompanionMode, access, server, lease, desktopHost, accessNotifications).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (lease.IsDenied) { }
                    failure = lease.Failure;
                }
                catch (CliAccessDeniedException exception) { failure = exception.Decision; }
                catch (OperationCanceledException) when (hostShutdown.IsCancellationRequested) { break; }
                catch (Exception exception) when (exception is not CliUsageException)
                {
                    output.WriteProgress($"Host recovery: {exception.GetBaseException().Message}");
                    failure = AccessDecision.Unavailable;
                }
                if (hostShutdown.IsCancellationRequested) break;
                access.Block(failure);
                await CliRuntime.WriteRecoveryMetadataAsync(options.DataDirectory, server?.Url,
                    CliLogging.CurrentLogFilePath, hostShutdown.Token).ConfigureAwait(false);
                await accessNotifications.UpdateAsync(failure, hostShutdown.Token).ConfigureAwait(false);
                if (lastFailureReason != failure.Reason)
                    output.WriteProgress($"{failure.Message} The host remains available. Explorer: {server?.Url}");
                lastFailureReason = failure.Reason;
                await access.WaitForRetryAsync(timeProvider, hostShutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (hostShutdown.IsCancellationRequested) { }
        finally
        {
            await hostShutdown.CancelAsync().ConfigureAwait(false);
            await desktopActions.ConfigureAwait(false);
            CliRuntime.TryDeleteMetadata(options.DataDirectory);
        }
        return CliExitCodes.Success;
    }

    internal static async Task RunDesktopActionsAsync(CliDesktopHost? desktopHost, CliArguments arguments,
        CliOutput output, CancellationTokenSource shutdown)
    {
        if (desktopHost is null) return;
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                var action = await desktopHost.WaitForRequestedActionAsync(shutdown.Token).ConfigureAwait(false);
                if (action == CliDesktopRequestedAction.Update)
                {
                    var available = await TryApplyDesktopUpdateAsync(output, shutdown.Token).ConfigureAwait(false);
                    await desktopHost.SetUpdateAvailableAsync(available, shutdown.Token).ConfigureAwait(false);
                    continue;
                }
                if (action == CliDesktopRequestedAction.Restart) StartDesktopRestartWaiter(arguments);
                await shutdown.CancelAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }

    internal static async Task<int> RunHostGenerationAsync(CliArguments arguments, CliOutput output,
        CancellationToken cancellationToken, CliRuntimeOptions options, bool noServe, string? pairAppId,
        CompanionAccessMode? requestedCompanionMode, CliLocalHostAccess access, CliLocalHostServer? server,
        CliAccessLease lease, CliDesktopHost? desktopHost, CliHostAccessNotifications accessNotifications)
    {
        await using var runtime = CliRuntime.CreateHostRuntime(options);
        await using var runnerControl = arguments.HasFlag("runner")
            ? Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<IRemoteRunnerControl>("Runner.CreateControl", [arguments, output, options, runtime, cancellationToken]) : null;
        runtime.RemoteRunnerControl = runnerControl;
        runtime.RemoteRunnerControlFactory = () => Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<IRemoteRunnerControl>("Runner.CreateControl", [arguments, output, options, runtime, cancellationToken]);
        EventHandler<RemoteRunnerStatusSnapshot>? runnerStatusChanged = null;
        if (requestedCompanionMode.HasValue) ConfigureCompanionRegistration(runtime, arguments, true);
        AttachRuntimeProgress(runtime, output);
        runtime.StatusChanged += (_, status) => output.WriteProgress(status);

        using var notificationCoordinator = desktopHost is null
            ? null
            : new NotificationCoordinator(runtime, desktopHost);

        try
        {
            // Subscribe before startup resumes app watches and begins new recordings.
            if (notificationCoordinator is not null)
                await notificationCoordinator.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await runtime.StartAsync(cancellationToken).ConfigureAwait(false);
            var companionHost = await TryStartLocalSimulatorHostAsync(
                    runtime,
                    options,
                    requestedCompanionMode,
                    startForLocalExplorer: !noServe,
                    output,
                    cancellationToken)
                .ConfigureAwait(false);
            await using var companionHostScope = companionHost;
            var companionAccess = companionHost?.AccessStatus;
            Uri? explorerUrl = null;
            if (!noServe)
            {
                var explorer = await runtime.SessionReplays.StartExplorerAsync(
                        new SessionExplorerStartRequest(
                            0, UrlPath: server?.Url.AbsolutePath),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!explorer.IsSuccess || explorer.ExplorerUrl is null)
                {
                    throw new InvalidOperationException(explorer.Message);
                }

                explorerUrl = explorer.ExplorerUrl;
            }

            access.Allow(explorerUrl, lease);
            explorerUrl = server?.Url;

            await using var controlServer = new ControlServer(
                runtime,
                options.DataDirectory,
                cancellationToken,
                CliAccessContext.Current?.Authorizer
                ?? throw new InvalidOperationException("Host startup requires a local runtime lifetime."),
                lease);
            controlServer.Start();
            if (desktopHost is not null)
            {
                runnerStatusChanged = (_, runnerStatus) =>
                    _ = UpdateDesktopRunnerStatusAsync(desktopHost, runnerStatus, cancellationToken);
                runtime.RemoteRunner.StatusChanged += runnerStatusChanged;
                await desktopHost.SetRunnerStatusAsync(runtime.RemoteRunner.GetSnapshot(), cancellationToken)
                    .ConfigureAwait(false);
            }
            if (arguments.HasFlag("runner"))
            {
                await runnerControl!.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            var status = runtime.GetStatusSnapshot();
            await CliRuntime.WriteMetadataAsync(
                options.DataDirectory,
                status,
                controlServer.PipeName,
                explorerUrl,
                companionAccess,
                CliLogging.CurrentLogFilePath,
                CancellationToken.None).ConfigureAwait(false);
            if (notificationCoordinator is not null)
            {
                await accessNotifications.UpdateAsync(lease.Decision, cancellationToken).ConfigureAwait(false);
                await notificationCoordinator.NotifyHostStartedAsync(cancellationToken).ConfigureAwait(false);
            }

            if (pairAppId is null)
            {
                var identity = CliReleaseIdentity.Current;
                output.Write(
                    new HostStartedOutput(
                        "ansight.host/v1",
                        Environment.ProcessId,
                        controlServer.PipeName,
                        status,
                        explorerUrl?.ToString(),
                        companionAccess,
                        identity.Version,
                        identity.BuildNumber,
                        identity.CommitSha,
                        CliLogging.CurrentLogFilePath),
                    () => BuildHostStartedMessage(
                        status,
                        explorerUrl,
                        companionAccess,
                        CliLogging.CurrentLogFilePath));
            }
            else
            {
                var pairingOutput = IssuePairing(
                    runtime,
                    pairAppId,
                    arguments,
                    status,
                    controlServer.PipeName,
                    explorerUrl,
                    companionAccess);
                output.Write(
                    pairingOutput,
                    () => BuildHostPairingStartedMessage(pairingOutput));
            }

            try
            {
                await runtime.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }

            if (!cancellationToken.IsCancellationRequested)
                throw new InvalidOperationException("The local host runtime stopped unexpectedly.");
            return CliExitCodes.Success;
        }
        finally
        {
            if (runnerControl is not null) await runnerControl.StopAsync(CancellationToken.None).ConfigureAwait(false);
            if (runnerStatusChanged is not null)
                runtime.RemoteRunner.StatusChanged -= runnerStatusChanged;
            access.Block(lease.IsDenied ? lease.Failure : AccessDecision.Unavailable);
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await runtime.StopAsync(stopTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                output.WriteProgress("Host shutdown exceeded the 10 second deadline.");
            }

        }
    }
internal static Task RunResidentRunnerAsync(
        CliArguments arguments,
        CliOutput output,
        CliRuntimeOptions options,
        RuntimeCoordinator runtime,
        CancellationToken cancellationToken) => Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<Task>("HostedHostCommands.RunResidentRunnerAsync", [arguments, output, options, runtime, cancellationToken]);

    internal static async Task UpdateDesktopRunnerStatusAsync(
        CliDesktopHost desktopHost,
        RemoteRunnerStatusSnapshot status,
        CancellationToken cancellationToken)
    {
        try
        {
            await desktopHost.SetRunnerStatusAsync(status, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    internal static async Task<bool> TryApplyDesktopUpdateAsync(
        CliOutput output,
        CancellationToken cancellationToken)
    {
        try
        {
            var installationState = CliInstallationReceiptStore.Load();
            using var httpClient = UpdateCommands.CreateHttpClient();
            var service = new CliUpdateService(
                httpClient,
                CliReleaseIdentity.Current,
                installationState,
                CliRuntimeIdentifier.Current);
            var status = await service.CheckAsync(
                    requestedChannel: null,
                    requestedVersion: null,
                    requestedBuildNumber: null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!status.IsUpdateAvailable)
            {
                output.WriteProgress("[update] Ansight is already current.");
                return false;
            }

            var result = await new CliInstallerRunner(httpClient, installationState)
                .ApplyAsync(
                    status,
                    UpdateCommands.CreateProgress(output),
                    cancellationToken)
                .ConfigureAwait(false);
            output.WriteProgress($"[update] {result.Message}");
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException
                                           or IOException
                                           or InvalidOperationException)
        {
            output.WriteProgress($"[update.warning] Update failed: {exception.Message}");
            return true;
        }
    }

    internal static void StartDesktopRestartWaiter(CliArguments arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveActiveCliExecutablePath(),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("host");
        startInfo.ArgumentList.Add("desktop-restart");
        startInfo.ArgumentList.Add("--previous-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        startInfo.Environment[DesktopRestartArgumentsEnvironmentVariable] =
            JsonSerializer.Serialize(arguments.OriginalArguments);
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException(
                                "The Ansight restart helper could not be started.");
    }

    internal static async Task<int> RunDesktopRestartAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(
            2,
            "ansight host desktop-restart --previous-pid <pid>");
        var previousProcessId = arguments.GetRequiredIntOption(
            "previous-pid",
            minimum: 1,
            maximum: int.MaxValue);
        try
        {
            using var previousProcess = Process.GetProcessById(previousProcessId);
            await previousProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // The previous host exited before the restart helper inspected it.
        }

        var restartArguments = ReadDesktopRestartArguments();
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveActiveCliExecutablePath(),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in restartArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException(
                                "The restarted Ansight host could not be started.");
        output.WriteProgress("Restarted Ansight.");
        return CliExitCodes.Success;
    }

    internal static IReadOnlyList<string> ReadDesktopRestartArguments()
    {
        var source = Environment.GetEnvironmentVariable(DesktopRestartArgumentsEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(source))
        {
            try
            {
                var arguments = JsonSerializer.Deserialize<string[]>(source);
                if (arguments is { Length: >= 2 }
                    && string.Equals(arguments[0], "host", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(arguments[1], "run", StringComparison.OrdinalIgnoreCase))
                {
                    return arguments;
                }
            }
            catch (JsonException)
            {
            }
        }

        return ["host", "run"];
    }

    internal static string ResolveActiveCliExecutablePath()
    {
        var installationState = CliInstallationReceiptStore.Load();
        var executableName = OperatingSystem.IsWindows() ? "ansight.exe" : "ansight";
        if (!string.IsNullOrWhiteSpace(installationState.Receipt?.InstallRoot))
        {
            var currentExecutable = Path.Combine(
                installationState.Receipt.InstallRoot,
                "current",
                executableName);
            if (File.Exists(currentExecutable))
            {
                return currentExecutable;
            }
        }

        if (!string.IsNullOrWhiteSpace(installationState.Receipt?.BinDirectory))
        {
            var linkedExecutable = Path.Combine(
                installationState.Receipt.BinDirectory,
                executableName);
            if (File.Exists(linkedExecutable))
            {
                return linkedExecutable;
            }
        }

        return Environment.ProcessPath
               ?? throw new InvalidOperationException(
                   "The active Ansight CLI executable could not be located.");
    }

    internal static HostPairingStartedOutput IssuePairing(
        RuntimeCoordinator runtime,
        string appId,
        CliArguments arguments,
        RuntimeStatusSnapshot status,
        string controlPipeName,
        Uri? explorerUrl,
        CompanionAccessStatus? companionAccess)
    {
        var pairing = runtime.Pairing.Issue(
            appId,
            arguments.GetOption("name"),
            arguments.GetOption("duration"));
        if (!pairing.IsSuccess || pairing.Invite is null)
        {
            throw new InvalidOperationException(pairing.Message);
        }

        var code = runtime.Pairing.CreateCode(
            pairing.Invite.InviteId,
            arguments.GetOption("host-address"));
        if (!code.IsSuccess || string.IsNullOrWhiteSpace(code.PairingCode))
        {
            runtime.Pairing.Revoke(pairing.Invite.InviteId);
            throw new InvalidOperationException(
                $"{code.Message} The unusable enrollment invite was revoked.");
        }

        var identity = CliReleaseIdentity.Current;
        return new HostPairingStartedOutput(
            "ansight.host-pairing/v1",
            Environment.ProcessId,
            controlPipeName,
            status,
            explorerUrl?.ToString(),
            pairing.Message,
            pairing.Invite,
            pairing.InviteFilePath,
            pairing.Duration,
            code,
            companionAccess,
            identity.Version,
            identity.BuildNumber,
            identity.CommitSha,
            CliLogging.CurrentLogFilePath);
    }

    internal static void AttachRuntimeProgress(
        RuntimeCoordinator runtime,
        CliOutput output)
    {
        runtime.PairingEventOccurred += (_, runtimeEvent) =>
        {
            switch (runtimeEvent.Kind)
            {
                case RuntimePairingEventKind.DiscoveryReceived:
                    output.WriteProgress(HostSessionProgressMessages.BuildPairingRequest(runtimeEvent));
                    break;
                case RuntimePairingEventKind.PairingAccepted:
                    output.WriteProgress(HostSessionProgressMessages.BuildPairingAccepted(runtimeEvent));
                    output.WriteProgress(HostSessionProgressMessages.BuildSessionCreated(runtimeEvent));
                    output.WriteProgress(HostSessionProgressMessages.BuildPortalSuggestion(runtimeEvent.SessionId));
                    break;
                case RuntimePairingEventKind.PairingRejected:
                    output.WriteProgress(HostSessionProgressMessages.BuildPairingRejected(runtimeEvent));
                    break;
            }
        };
        runtime.SessionCaptureEventOccurred += (_, runtimeEvent) =>
        {
            if (runtimeEvent.Kind == RuntimeSessionCaptureEventKind.Started)
            {
                output.WriteProgress(HostSessionProgressMessages.BuildCaptureStarted(runtimeEvent));
            }
            else if (runtimeEvent.Kind == RuntimeSessionCaptureEventKind.Stopped)
            {
                output.WriteProgress(HostSessionProgressMessages.BuildCaptureStopped(runtimeEvent));
            }
        };
    }

    internal static string BuildHostStartedMessage(
        RuntimeStatusSnapshot status,
        Uri? explorerUrl,
        CompanionAccessStatus? companionAccess = null,
        string? logFilePath = null)
    {
        var identity = CliReleaseIdentity.Current;
        var explorer = explorerUrl is null
            ? string.Empty
            : $"Explorer: {explorerUrl}\n";
        var companion = companionAccess is null
            ? string.Empty
            : $"Companion: {FormatCompanionAccessMode(companionAccess.Mode)} ({companionAccess.Status})\n";
        return $"Ansight host is running (PID {Environment.ProcessId}).\n"
               + $"Version: {identity.Version} ({identity.BuildNumber}, {FormatCommitSha(identity.CommitSha)})\n"
               + explorer
               + companion
               + (string.IsNullOrWhiteSpace(logFilePath) ? string.Empty : $"Logs: {logFilePath}\n")
               + $"Data: {status.BaseFolderPath}";
    }

    internal static string BuildHostPairingStartedMessage(HostPairingStartedOutput output)
        => $"{BuildHostStartedMessage(
            output.Status,
            string.IsNullOrWhiteSpace(output.ExplorerUrl) ? null : new Uri(output.ExplorerUrl),
            output.CompanionAccess,
            output.LogFilePath)}\n\n"
           + $"{output.Message}\n"
           + $"Expires: {output.Invite.ExpiresAtUtc:O}\n"
           + $"Pairing code (one-time credential; keep private):\n{output.Code.PairingCode}\n"
           + $"QR (scan from the Ansight-enabled Debug app):\n{CliQrTerminalRenderer.Render(output.Code.PairingCode!)}\n"
           + "Keep this command running. Pairing and live capture status will appear below.";

    internal static int ShowStatus(CliArguments arguments, CliOutput output)
    {
        var options = CliRuntime.ResolveOptions(arguments);
        var metadata = CliRuntime.ReadMetadata(options.DataDirectory);
        var isRunning = metadata is not null && CliRuntime.IsHostProcessRunning(metadata);
        var result = new HostStatusOutput(
            "ansight.host-status/v1",
            isRunning,
            metadata?.ProcessId,
            metadata?.ControlPipeName,
            options.DataDirectory,
            metadata?.UpdatedUtc,
            metadata is not null && !isRunning,
            metadata?.ExplorerUrl,
            metadata?.CompanionAccess,
            metadata?.CliVersion,
            metadata?.CliBuildNumber,
            metadata?.CommitSha,
            metadata?.LogFilePath);
        output.Write(
            result,
            () => isRunning
                ? BuildHostStatusMessage(metadata!)
                : "Ansight host is not running.");
        return isRunning ? CliExitCodes.Success : CliExitCodes.HostUnavailable;
    }

    internal static string BuildHostStatusMessage(CliHostMetadata metadata)
    {
        var explorer = string.IsNullOrWhiteSpace(metadata.ExplorerUrl)
            ? string.Empty
            : $"\nExplorer: {metadata.ExplorerUrl}";
        var companion = metadata.CompanionAccess is null
            ? string.Empty
            : $"\nCompanion: {FormatCompanionAccessMode(metadata.CompanionAccess.Mode)} ({metadata.CompanionAccess.Status})";
        var version = string.IsNullOrWhiteSpace(metadata.CliVersion)
            ? "\nVersion: unknown (host predates version reporting)"
            : $"\nVersion: {metadata.CliVersion} ({metadata.CliBuildNumber?.ToString() ?? "unknown"}, {FormatCommitSha(metadata.CommitSha)})";
        var logs = string.IsNullOrWhiteSpace(metadata.LogFilePath)
            ? string.Empty
            : $"\nLogs: {metadata.LogFilePath}";
        return $"Ansight host is running (PID {metadata.ProcessId}).{version}{explorer}{companion}{logs}";
    }

    internal static int ShowLogs(CliArguments arguments, CliOutput output)
    {
        arguments.EnsurePositionalCount(2, "ansight host logs");
        var options = CliRuntime.ResolveOptions(arguments);
        var metadata = CliRuntime.ReadMetadata(options.DataDirectory);
        var isRunning = metadata is not null && CliRuntime.IsHostProcessRunning(metadata);
        var logDirectory = Path.Combine(options.DataDirectory, Ansight.Infrastructure.Logging.LoggingConstants.LogsFolderName);
        var currentLogFilePath = ResolveHostLogFilePath(logDirectory, metadata);
        var result = new HostLogsOutput(
            "ansight.host-logs/v1",
            isRunning,
            options.DataDirectory,
            logDirectory,
            currentLogFilePath);
        output.Write(result, () => RenderHostLogs(result));
        return CliExitCodes.Success;
    }

    internal static string? ResolveHostLogFilePath(
        string logDirectory,
        CliHostMetadata? metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata?.LogFilePath)
            && File.Exists(metadata.LogFilePath))
        {
            return metadata.LogFilePath;
        }

        try
        {
            return Directory.Exists(logDirectory)
                ? Directory.EnumerateFiles(logDirectory, "host-*.log", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault()
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string RenderHostLogs(HostLogsOutput result)
    {
        var current = string.IsNullOrWhiteSpace(result.CurrentLogFilePath)
            ? "Current: no persisted CLI-host log has been created yet"
            : $"Current: {result.CurrentLogFilePath}";
        return $"Host logs: {result.LogDirectory}\n"
               + $"{current}\n"
               + "Captured app logs: ansight session list --has-logs; "
               + "then ansight session logs <session-id>";
    }

    internal static string FormatCommitSha(string? commitSha)
        => string.IsNullOrWhiteSpace(commitSha)
            ? "commit unknown"
            : $"commit {commitSha[..Math.Min(8, commitSha.Length)]}";

    internal static async Task<int> StopHostAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        var options = CliRuntime.ResolveOptions(arguments);
        var metadata = CliRuntime.ReadMetadata(options.DataDirectory);
        if (metadata is null || !CliRuntime.IsHostProcessRunning(metadata))
        {
            throw new CliHostUnavailableException("Ansight host is not running.");
        }

        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Graceful host stop is not implemented on Windows yet. Send Ctrl+C to the host process.");
        }

        if (NativeMethods.Kill(metadata.ProcessId, NativeMethods.SigTerm) != 0)
        {
            throw new InvalidOperationException(
                $"Could not send SIGTERM to Ansight host PID {metadata.ProcessId}. errno={Marshal.GetLastPInvokeError()}.");
        }

        var timeoutAtUtc = DateTimeOffset.UtcNow.AddSeconds(10);
        while (CliRuntime.IsHostProcessRunning(metadata) && DateTimeOffset.UtcNow < timeoutAtUtc)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }

        var stopped = !CliRuntime.IsHostProcessRunning(metadata);
        output.Write(
            new HostStoppedOutput("ansight.host-stop/v1", metadata.ProcessId, stopped),
            () => stopped
                ? $"Ansight host PID {metadata.ProcessId} stopped."
                : $"SIGTERM sent to Ansight host PID {metadata.ProcessId}; it is still shutting down.");
        return stopped ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    internal static string BuildHelp()
        => """
           Run and manage the reusable Ansight host

           Local capture, inspection, replay and test execution are free and require no account.
           The local host runs independently of cloud login and subscription status.
           Account-backed companion and cloud operations prompt for sign-in separately.

           Usage:
             ansight host run [--runner] [--pair <app-id>] [options]
             ansight host status [options]
             ansight host logs [options]
             ansight host stop [options]

           Commands:
             run       Start the long-lived SDK, explorer, and CLI control host in the foreground.
                       Subsequent CLI commands reuse it through a current-user-only control pipe.
                       Packaged macOS and Windows hosts always show the native Ansight tray icon.
             status    Report whether the resident host is running and show its local explorer link.
             logs      Show the host log directory and current or most recent CLI-host log; alias: log.
             stop      Gracefully stop the resident host and release its ports and data-directory lock.

           Important options:
             --pair <app-id>                  Start the host and show a terminal pairing QR
             --runner                         Poll and execute remote jobs in this resident host
             --poll-seconds <n>               Runner queue interval; default 5 seconds
             --skip-build-preflight           Skip the runner's repeated post-sync build check
             --name <name>                    Name the generated enrollment invitation
             --duration <duration>            Pairing lifetime, for example 15m or 1h
             --host-address <LAN-IP>           Address a phone can use to reach this host
             --data-dir <path>                Override the shared Ansight state directory
             --discovery-port <port>          UDP SDK discovery port
             --websocket-port <port>          SDK WebSocket port
             --serve-port <port>              Local explorer HTTP port; 0 chooses an available port
             --serve-path <path>              Use / or a fixed URL-safe segment
             --open                           Open the local explorer in the default browser
             --no-serve                       Run the host without starting the local explorer
             --companion-access [session|always]
                                              Publish simulators to the signed-in companion app (macOS)
             --machine-name <name>             Required machine name for companion registration
             --team-id <uuid>                  Organisation for companion access; optional when only one is eligible
             --enable-repository-automations  Allow trusted repository triggers (default)
             --disable-repository-automations Disable repository triggers for this host
             --automation-repository <path>   Connect a repository at startup; may be repeated
             --json                           Emit versioned machine-readable output
             --verbose                        Include host diagnostics and telemetry

           Examples:
             ansight host run --open
             ansight host run --runner
             ansight host run --serve-path /
             ansight host run --no-serve
             ansight host run --companion-access session --machine-name "Development Mac"
             ansight host run --pair com.example.app
             ansight host run --pair com.example.app --duration 30m --host-address 192.168.1.20
             ansight host run --automation-repository /path/to/repo
             ansight host status --json
             ansight host logs
             ansight host stop

           Each loaded trigger declares an app ID. App and session events carry that ID, so the
           host dispatches only matching triggers. `repo automation connect` adds a trusted
           app/repository mapping for the lifetime of the running resident host.

           Agent session discovery:
             CLI:  ansight session list --connected --json
             UI:   ansight ui snapshot --session <session-id> --json

           The default local URL is http://127.0.0.1:47231/ansight/. Change it with
           `ansight config set explorer-path <path>` and `ansight config set explorer-port <port>`,
           or unset either setting to use a random path or available port. Explicit --serve-path
           and --serve-port values always take precedence.
           """;
internal static void ConfigureCompanionRegistration(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        bool companionAccessRequested) => Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<object?>("HostedHostCommands.ConfigureCompanionRegistration", [runtime, arguments, companionAccessRequested]);

    internal static CompanionAccessMode? ParseCompanionAccessMode(CliArguments arguments)
    {
        if (!arguments.HasFlag("companion-access"))
        {
            return null;
        }

        return (arguments.GetOption("companion-access") ?? "session").Trim().ToLowerInvariant() switch
        {
            "session" or "once" => CompanionAccessMode.Session,
            "always" => CompanionAccessMode.Always,
            _ => throw new CliUsageException("--companion-access must be session or always.")
        };
    }

    internal static async Task<CliLocalSimulatorHost?> TryStartLocalSimulatorHostAsync(
        RuntimeCoordinator runtime,
        CliRuntimeOptions options,
        CompanionAccessMode? requestedMode,
        bool startForLocalExplorer,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        var mode = requestedMode ?? CompanionAccessMode.Disabled;
        if (!OperatingSystem.IsMacOS())
        {
            if (requestedMode.HasValue)
            {
                throw new PlatformNotSupportedException(
                    "CLI companion hosting currently requires macOS for simulator video and input.");
            }

            return null;
        }

        if (!startForLocalExplorer && !requestedMode.HasValue && mode == CompanionAccessMode.Disabled)
        {
            return null;
        }

        try
        {
            return await CliLocalSimulatorHost.StartAsync(
                    runtime,
                    CliRuntime.CreateHostOptions(options),
                    mode,
                    output.WriteProgress,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!requestedMode.HasValue)
        {
            var status = $"Companion access is unavailable: {exception.Message}";
            output.WriteProgress(status);
            return null;
        }
    }

    internal static string FormatCompanionAccessMode(CompanionAccessMode mode)
        => mode switch
        {
            CompanionAccessMode.Session => "session",
            CompanionAccessMode.Always => "always",
            _ => "disabled"
        };

}
