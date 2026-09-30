using System.ComponentModel;
using System.Diagnostics;

namespace Ansight.Host.Files;

internal static class ArtifactDesktopActions
{
    public static async Task RevealAsync(string filePath, CancellationToken cancellationToken)
    {
        filePath = Path.GetFullPath(filePath);
        if (OperatingSystem.IsMacOS())
        {
            await RunAsync("/usr/bin/open", ["-R", filePath], cancellationToken).ConfigureAwait(false);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            await ArtifactPlatformApplications.RevealWindowsAsync(filePath, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                await RunAsync(
                        "dbus-send",
                        ["--session", "--print-reply", "--dest=org.freedesktop.FileManager1",
                            "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems",
                            $"array:string:{new Uri(filePath).AbsoluteUri}", "string:"],
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                await RunAsync("xdg-open", [Path.GetDirectoryName(filePath)!], cancellationToken)
                    .ConfigureAwait(false);
            }
            return;
        }

        throw new PlatformNotSupportedException("Showing artifacts in a folder is unavailable on this operating system.");
    }

    public static Task<string> ExportToDesktopAsync(string filePath, CancellationToken cancellationToken)
    {
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktopPath))
        {
            throw new InvalidOperationException("The operating system has not configured a Desktop folder.");
        }

        return ExportToDirectoryAsync(filePath, desktopPath, cancellationToken);
    }

    internal static async Task<string> ExportToDirectoryAsync(
        string filePath,
        string directoryPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var source = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        Directory.CreateDirectory(directoryPath);
        var name = Path.GetFileName(filePath);
        var extension = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);

        for (var suffix = 0; ; suffix++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationPath = Path.Combine(directoryPath, suffix == 0 ? name : $"{stem} ({suffix}){extension}");
            FileStream destination;
            try
            {
                destination = new FileStream(
                    destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
            }
            catch (IOException) when (File.Exists(destinationPath) || Directory.Exists(destinationPath))
            {
                continue;
            }

            try
            {
                await using (destination)
                {
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                }
                return destinationPath;
            }
            catch
            {
                File.Delete(destinationPath);
                throw;
            }
        }
    }

    private static async Task RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The operating system could not open its file manager.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, error).ConfigureAwait(false);
            var errorText = await error.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorText)
                    ? "The operating system could not show the artifact in a folder."
                    : errorText.Trim());
            }
        }
        catch (OperationCanceledException)
        {
            ArtifactPlatformApplications.StopLauncher(process);
            if (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("The operating system did not respond when opening the folder.");
            }
            throw;
        }
    }
}
