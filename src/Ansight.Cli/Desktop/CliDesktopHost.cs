using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Ansight.Host;

namespace Ansight.Cli.Desktop;

internal sealed class CliDesktopHost : INotificationSink, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Process? process;
    private readonly TcpClient? connection;
    private readonly StreamReader actionReader;
    private readonly StreamWriter commandWriter;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly Channel<CliDesktopRequestedAction> requestedActions =
        Channel.CreateUnbounded<CliDesktopRequestedAction>();
    private readonly Task actionReaderTask;
    private readonly Task errorReaderTask;
    private bool disposed;

    private CliDesktopHost(Process process, Action<string> writeProgress)
    {
        this.process = process;
        actionReader = process.StandardOutput;
        commandWriter = process.StandardInput;
        actionReaderTask = ReadActionsAsync(shutdown.Token);
        errorReaderTask = ReadErrorsAsync(writeProgress, shutdown.Token);
    }

    private CliDesktopHost(TcpClient connection)
    {
        this.connection = connection;
        var stream = connection.GetStream();
        actionReader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        commandWriter = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true);
        actionReaderTask = ReadActionsAsync(shutdown.Token);
        errorReaderTask = Task.CompletedTask;
    }

    public static async Task<CliDesktopHost?> TryStartAsync(
        Uri? explorerUrl,
        bool updateAvailable,
        Action<string> writeProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeProgress);
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
        {
            return null;
        }

        if (OperatingSystem.IsMacOS())
        {
            return await TryStartMacOSAsync(
                explorerUrl,
                updateAvailable,
                writeProgress,
                cancellationToken).ConfigureAwait(false);
        }

        var helperPath = ResolveWindowsHelperPath();
        if (helperPath is null)
        {
            writeProgress(
                "Desktop integration warning: the native tray helper was not found; "
                + "continuing without a tray icon or local notifications.");
            return null;
        }

        Process? process = null;
        CliDesktopHost? desktopHost = null;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = helperPath,
                WorkingDirectory = Path.GetDirectoryName(helperPath)!,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return null;
            }

            desktopHost = new CliDesktopHost(process, writeProgress);
            await desktopHost.SendCommandAsync(
                new CliDesktopCommand(
                    CliDesktopProtocol.CommandSchema,
                    "initialize",
                    explorerUrl?.AbsoluteUri,
                    UpdateAvailable: updateAvailable),
                cancellationToken).ConfigureAwait(false);
            return desktopHost;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                           or IOException
                                           or System.ComponentModel.Win32Exception)
        {
            writeProgress($"Desktop integration warning: {exception.GetBaseException().Message}");
            if (desktopHost is not null)
            {
                await desktopHost.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                process?.Dispose();
            }

            return null;
        }
    }

    private static async Task<CliDesktopHost?> TryStartMacOSAsync(
        Uri? explorerUrl,
        bool updateAvailable,
        Action<string> writeProgress,
        CancellationToken cancellationToken)
    {
        var appPath = ResolveMacOSHelperAppPath();
        if (appPath is null)
        {
            writeProgress(
                "Desktop integration warning: the native tray helper was not found; "
                + "continuing without a tray icon or local notifications.");
            return null;
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        Process? launcher = null;
        TcpClient? connection = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/open",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add("-g");
            startInfo.ArgumentList.Add(appPath);
            startInfo.ArgumentList.Add("--args");
            startInfo.ArgumentList.Add("--ansight-desktop-port");
            startInfo.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--ansight-desktop-token");
            startInfo.ArgumentList.Add(token);

            launcher = Process.Start(startInfo);
            if (launcher is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            connection = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
            connection.NoDelay = true;

            var handshake = await ReadHandshakeAsync(
                connection.GetStream(),
                timeout.Token).ConfigureAwait(false);
            var action = JsonSerializer.Deserialize<CliDesktopAction>(handshake, JsonOptions);
            if (action is null
                || !string.Equals(action.Schema, CliDesktopProtocol.ActionSchema, StringComparison.Ordinal)
                || !string.Equals(action.Action, "ready", StringComparison.Ordinal)
                || !string.Equals(action.Token, token, StringComparison.Ordinal))
            {
                writeProgress("Desktop integration warning: the native tray helper returned an invalid handshake.");
                connection.Dispose();
                return null;
            }

            var desktopHost = new CliDesktopHost(connection);
            connection = null;
            await desktopHost.SendCommandAsync(
                new CliDesktopCommand(
                    CliDesktopProtocol.CommandSchema,
                    "initialize",
                    explorerUrl?.AbsoluteUri,
                    UpdateAvailable: updateAvailable),
                cancellationToken).ConfigureAwait(false);
            return desktopHost;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var launcherError = launcher is null
                ? null
                : await launcher.StandardError.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false);
            writeProgress(
                string.IsNullOrWhiteSpace(launcherError)
                    ? "Desktop integration warning: the native tray helper did not connect in time."
                    : $"Desktop integration warning: {launcherError.Trim()}");
            connection?.Dispose();
            return null;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                           or IOException
                                           or JsonException
                                           or System.ComponentModel.Win32Exception
                                           or SocketException)
        {
            writeProgress($"Desktop integration warning: {exception.GetBaseException().Message}");
            connection?.Dispose();
            return null;
        }
        finally
        {
            launcher?.Dispose();
        }
    }

    private static async Task<string> ReadHandshakeAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        using var contents = new MemoryStream();
        while (contents.Length < 4096)
        {
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                throw new IOException("The native tray helper closed before completing its handshake.");
            }

            if (buffer[0] == (byte)'\n')
            {
                return Encoding.UTF8.GetString(contents.GetBuffer(), 0, checked((int)contents.Length));
            }

            contents.WriteByte(buffer[0]);
        }

        throw new IOException("The native tray helper returned an oversized handshake.");
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task ShowAsync(
        Notification notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return SendCommandAsync(
            new CliDesktopCommand(
                CliDesktopProtocol.CommandSchema,
                "notify",
                Identifier: notification.Identifier,
                Title: notification.Title,
                Body: notification.Body,
                SessionId: notification.SessionId,
                DeliveryDelaySeconds: notification.Schedule?.Delay.TotalSeconds,
                RepeatIntervalSeconds: notification.Schedule?.RepeatInterval?.TotalSeconds,
                PreserveExisting: notification.Schedule?.PreserveExisting == true,
                NotificationActionIdentifier: notification.Action?.Identifier,
                NotificationActionTitle: notification.Action?.Title,
                NotificationActionDeferralSeconds: notification.Action?.DeferralInterval.TotalSeconds),
            cancellationToken);
    }

    public Task RemoveAsync(
        string notificationIdentifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notificationIdentifier);
        return SendCommandAsync(
            new CliDesktopCommand(
                CliDesktopProtocol.CommandSchema,
                "remove-notification",
                Identifier: notificationIdentifier.Trim()),
            cancellationToken);
    }

    public Task<CliDesktopRequestedAction> WaitForRequestedActionAsync(
        CancellationToken cancellationToken) =>
        requestedActions.Reader.ReadAsync(cancellationToken).AsTask();

    public Task SetUpdateAvailableAsync(
        bool updateAvailable,
        CancellationToken cancellationToken = default) =>
        SendCommandAsync(
            new CliDesktopCommand(
                CliDesktopProtocol.CommandSchema,
                "update-availability",
                UpdateAvailable: updateAvailable),
            cancellationToken);

    public Task SetRunnerStatusAsync(
        RemoteRunnerStatusSnapshot status,
        CancellationToken cancellationToken = default) =>
        SendCommandAsync(
            new CliDesktopCommand(
                CliDesktopProtocol.CommandSchema,
                "runner-status",
                RunnerState: status.State,
                RunnerDetail: status.Message),
            cancellationToken);

    private async Task SendCommandAsync(
        CliDesktopCommand command,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await commandWriter.WriteLineAsync(
                JsonSerializer.Serialize(command, JsonOptions).AsMemory(),
                cancellationToken).ConfigureAwait(false);
            await commandWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private async Task ReadActionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await actionReader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                CliDesktopAction? action;
                try
                {
                    action = JsonSerializer.Deserialize<CliDesktopAction>(line, JsonOptions);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (TryResolveRequestedAction(action, out var requestedAction))
                {
                    requestedActions.Writer.TryWrite(requestedAction);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            if (!disposed)
            {
                // The resident host and its tray helper share a lifetime. If the
                // helper exits unexpectedly, stop the host instead of leaving a
                // tray-less resident process behind.
                requestedActions.Writer.TryWrite(CliDesktopRequestedAction.Stop);
            }
        }
    }

    internal static bool TryResolveRequestedAction(
        CliDesktopAction? action,
        out CliDesktopRequestedAction requestedAction)
    {
        var actionName = action?.Action?.ToLowerInvariant();
        requestedAction = actionName switch
        {
            "stop" => CliDesktopRequestedAction.Stop,
            "restart" => CliDesktopRequestedAction.Restart,
            "update" => CliDesktopRequestedAction.Update,
            _ => default
        };
        return action is not null
               && string.Equals(action.Schema, CliDesktopProtocol.ActionSchema, StringComparison.Ordinal)
               && actionName is "stop" or "restart" or "update";
    }

    private async Task ReadErrorsAsync(
        Action<string> writeProgress,
        CancellationToken cancellationToken)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    writeProgress($"Desktop integration: {line}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
    }

    private static string? ResolveMacOSHelperAppPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("ANSIGHT_DESKTOP_HELPER");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var fullConfiguredPath = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(configuredPath.Trim()));
            if (Directory.Exists(fullConfiguredPath)
                && string.Equals(Path.GetExtension(fullConfiguredPath), ".app", StringComparison.OrdinalIgnoreCase))
            {
                return fullConfiguredPath;
            }

            if (File.Exists(fullConfiguredPath))
            {
                var directory = new DirectoryInfo(Path.GetDirectoryName(fullConfiguredPath)!);
                while (directory is not null)
                {
                    if (string.Equals(directory.Extension, ".app", StringComparison.OrdinalIgnoreCase))
                    {
                        return directory.FullName;
                    }

                    directory = directory.Parent;
                }
            }

            return null;
        }

        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "Ansight Tray.app"),
            Path.Combine(baseDirectory, "ansight-tray.app")
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static string? ResolveWindowsHelperPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("ANSIGHT_DESKTOP_HELPER");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var fullConfiguredPath = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(configuredPath.Trim()));
            return File.Exists(fullConfiguredPath) ? fullConfiguredPath : null;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "ansight-tray.exe");
        return File.Exists(path) ? path : null;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            await writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (process is null || !process.HasExited)
                {
                    await commandWriter.WriteLineAsync(
                        JsonSerializer.Serialize(
                            new CliDesktopCommand(CliDesktopProtocol.CommandSchema, "shutdown"),
                            JsonOptions)).ConfigureAwait(false);
                    await commandWriter.FlushAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                writeGate.Release();
            }

            if (process is not null && !process.HasExited)
            {
                var exitTask = process.WaitForExitAsync();
                if (await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false) != exitTask)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            else if (process is null)
            {
                var exitTask = actionReaderTask;
                if (await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false) != exitTask)
                {
                    connection?.Dispose();
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
        }
        finally
        {
            shutdown.Cancel();
            try
            {
                await Task.WhenAll(actionReaderTask, errorReaderTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            commandWriter.Dispose();
            actionReader.Dispose();
            connection?.Dispose();
            process?.Dispose();
            shutdown.Dispose();
            writeGate.Dispose();
        }
    }
}
