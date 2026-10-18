using Ansight.Host;

namespace Ansight.Cli.Commands.Host;

internal static partial class HostCommands
{
    private sealed record HostStartAttempt(CliDataDirectoryLock? DataLock, int ExitCode);

    private static async Task<HostStartAttempt> AcquireHostLockAsync(CliArguments arguments,
        CliOutput output, string dataDirectory, CancellationToken cancellationToken)
    {
        try { return new(CliDataDirectoryLock.Acquire(dataDirectory), CliExitCodes.Success); }
        catch (CliHostUnavailableException)
        {
            // A concurrent starter may own the lock before publishing its control address.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                if (await ControlClient.TryRunHostCommandAsync(arguments, output, cancellationToken).ConfigureAwait(false) is { } code)
                    return new(null, code);
            }
            throw;
        }
    }

    internal static int ReuseHost(CliArguments requested, CliArguments startup, RuntimeCoordinator? runtime, CliOutput output)
    {
        requested.EnsurePositionalCount(2, "ansight host run");
        if (runtime is null)
            throw new CliHostUnavailableException("The existing host is starting or recovering. Retry shortly, or use 'ansight host stop'.");
        var options = CliRuntime.ResolveOptions(startup);
        var metadata = CliRuntime.ReadMetadata(options.DataDirectory)
            ?? throw new CliHostUnavailableException("The host is starting. Retry shortly.");
        ValidateReuseOptions(requested, startup, metadata);
        if (requested.HasFlag("open"))
        {
            if (metadata.ExplorerUrl is null)
                throw new CliUsageException("The running host uses --no-serve. Stop and restart it to open the player.");
            if (CliBrowserLauncher.TryOpen(new Uri(metadata.ExplorerUrl)) is { } warning) output.WriteProgress(warning);
        }
        if (requested.GetOption("pair") is { } appId)
        {
            var pairing = IssuePairing(runtime, appId, requested, runtime.GetStatusSnapshot(), metadata.ControlPipeName!,
                metadata.ExplorerUrl is null ? null : new Uri(metadata.ExplorerUrl), metadata.CompanionAccess);
            output.Write(pairing, () => BuildHostPairingStartedMessage(pairing));
        }
        else
        {
            output.Write(new HostStartedOutput("ansight.host/v1", metadata.ProcessId, metadata.ControlPipeName!,
                runtime.GetStatusSnapshot(), metadata.ExplorerUrl, metadata.CompanionAccess,
                metadata.CliVersion, metadata.CliBuildNumber, metadata.CommitSha, metadata.LogFilePath),
                () => "Reusing the existing host.\n" + BuildHostStatusMessage(metadata));
        }
        return CliExitCodes.Success;
    }

    internal static void ValidateReuseOptions(CliArguments requested, CliArguments startup, CliHostMetadata metadata)
    {
        var options = CliRuntime.ResolveOptions(requested);
        var running = CliRuntime.ResolveOptions(startup);
        foreach (var flag in new[] { "runner", "no-serve", "skip-build-preflight" })
            if (requested.HasFlag(flag) && !startup.HasFlag(flag)) Conflict(flag);
        if ((requested.HasFlag("enable-repository-automations") || requested.HasFlag("disable-repository-automations"))
            && options.EnableRepositoryAutomations != running.EnableRepositoryAutomations) Conflict("disable-repository-automations");
        foreach (var option in new[] { "companion-access", "poll-seconds", "machine-name" })
            if (requested.HasFlag(option) && !requested.GetOptions(option).SequenceEqual(startup.GetOptions(option))) Conflict(option);
        Check("node-path", options.JavaScriptExecutablePath, running.JavaScriptExecutablePath);
        if (requested.HasFlag("automation-repository") && !options.AutomationRepositoryPaths.SequenceEqual(running.AutomationRepositoryPaths))
            Conflict("automation-repository");
        Check("discovery-port", options.DiscoveryPort, running.DiscoveryPort);
        Check("websocket-port", options.WebSocketPort, running.WebSocketPort);
        Check("adb-path", options.AdbPath, running.AdbPath);
        Check("xcode-path", options.XcodePath, running.XcodePath);
        Check("secret-store-file", options.SecureStorageFilePath, running.SecureStorageFilePath);
        Check("secret-key-file", options.SecureStorageKeyFilePath, running.SecureStorageKeyFilePath);
        if (requested.GetOption("serve-port") is { } port && port != "0"
            && (!Uri.TryCreate(metadata.ExplorerUrl, UriKind.Absolute, out var url) || url.Port.ToString() != port)) Conflict("serve-port");
        if (requested.GetOption("serve-path") is { } path
            && (!Uri.TryCreate(metadata.ExplorerUrl, UriKind.Absolute, out var pathUrl) || pathUrl.AbsolutePath != ExplorerServer.NormalizeRequestedPath(path))) Conflict("serve-path");

        void Check<T>(string name, T desired, T current)
        {
            if (requested.HasFlag(name) && !EqualityComparer<T>.Default.Equals(desired, current)) Conflict(name);
        }
        static void Conflict(string name)
            => throw new CliUsageException($"--{name} conflicts with the running host. Run 'ansight host stop' for this data directory, then restart with the requested settings.");
    }

    private static bool HasHostStopped(string dataDirectory, CliHostMetadata expected)
    {
        if (!CliRuntime.IsHostProcessRunning(expected)) return true;
        // Tests and embedded hosts can share a process. Lock release proves cleanup completed.
        try
        {
            using var available = CliDataDirectoryLock.Acquire(dataDirectory);
            return true;
        }
        catch (CliHostUnavailableException) { return false; }
    }
}
