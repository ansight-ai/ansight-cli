using System.Diagnostics;

namespace Ansight.Cli.LocalSimulator;

internal sealed class CodexRpcConnectionFactory : ICodexRpcConnectionFactory
{
    private readonly SemaphoreSlim startupGate = new(1, 1);

    public async Task QueueAsync(string threadId, string prompt, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(ResolveExecutable())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("queue");
        start.ArgumentList.Add("--thread");
        start.ArgumentList.Add(threadId);
        start.ArgumentList.Add("--message");
        start.ArgumentList.Add(prompt);
        start.ArgumentList.Add("--resume");
        var configuredSocket = Environment.GetEnvironmentVariable("ANSIGHT_CODEX_SOCKET");

        if (!string.IsNullOrWhiteSpace(configuredSocket))
        {
            start.ArgumentList.Add("--remote");
            start.ArgumentList.Add("unix://" + configuredSocket);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = Process.Start(start) ?? throw new IOException("Could not queue annotations in Codex.");
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var response = await output.ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            var acknowledged = response.Split('\n').Any(line =>
                line.StartsWith("Queued message ", StringComparison.Ordinal)
                && line.TrimEnd().EndsWith($" for thread {threadId}.", StringComparison.Ordinal));

            if (process.ExitCode != 0 || !acknowledged)
            {
                throw new IOException("Codex did not acknowledge the queued annotations. Check the task before sending again.");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    public async Task<ICodexRpcConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var configuredSocket = Environment.GetEnvironmentVariable("ANSIGHT_CODEX_SOCKET");
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var directory = string.IsNullOrWhiteSpace(codexHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : codexHome;
        var socketPath = string.IsNullOrWhiteSpace(configuredSocket)
            ? Path.Combine(directory, "app-server-control", "app-server-control.sock")
            : configuredSocket;

        if (string.IsNullOrWhiteSpace(configuredSocket))
        {
            await startupGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                // Codex starts its shared daemon only when needed. Starting a separate
                // stdio server would lose the live turn state needed for steering.
                if (!File.Exists(socketPath))
                {
                    await StartDaemonAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                startupGate.Release();
            }
        }

        try
        {
            return await CodexRpcConnection.ConnectAsync(socketPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new IOException("Unable to connect to Codex on this Mac. Check that Codex is installed and signed in, then retry. "
                + error.Message, error);
        }
    }

    private static async Task StartDaemonAsync(CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(ResolveExecutable())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("app-server");
        start.ArgumentList.Add("daemon");
        start.ArgumentList.Add("start");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = Process.Start(start) ?? throw new IOException("Could not start Codex on this Mac.");
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(output, errors).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new IOException("Codex could not start its shared server. Update Codex and retry.");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private static string ResolveExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("ANSIGHT_CODEX_EXECUTABLE");

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => Path.Combine(path, OperatingSystem.IsWindows() ? "codex.exe" : "codex"))
            .Concat([
                "/Applications/Codex.app/Contents/Resources/codex",
                "/Applications/ChatGPT.app/Contents/Resources/codex-cli/CodexCLI.app/Contents/MacOS/codex"
            ]);

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Install and sign in to Codex on this Mac, then retry from the companion.");
    }
}
