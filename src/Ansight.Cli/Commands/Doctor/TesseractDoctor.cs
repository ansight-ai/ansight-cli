using System.Diagnostics;

namespace Ansight.Cli.Commands.Doctor;

internal static class TesseractDoctor
{
    internal const string ExecutablePathEnvironmentVariable = "ANSIGHT_TESSERACT_PATH";
    private static readonly TimeSpan versionTimeout = TimeSpan.FromSeconds(5);

    public static async Task<DoctorCheck> CheckAsync(
        string? configuredExecutablePath,
        string? inheritedPath,
        CancellationToken cancellationToken)
    {
        string? executablePath;
        try
        {
            executablePath = ResolveExecutablePath(configuredExecutablePath, inheritedPath);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or NotSupportedException
                                           or PathTooLongException)
        {
            return new DoctorCheck(
                "tool.tesseract",
                "invalid-configuration",
                false,
                false,
                $"{ExecutablePathEnvironmentVariable} is not a valid executable path: {exception.Message}",
                configuredExecutablePath?.Trim());
        }

        if (executablePath is null)
        {
            var configured = !string.IsNullOrWhiteSpace(configuredExecutablePath);
            return new DoctorCheck(
                "tool.tesseract",
                configured ? "configured-path-missing" : "unavailable",
                false,
                false,
                configured
                    ? $"{ExecutablePathEnvironmentVariable} does not point to an existing Tesseract executable."
                    : BuildInstallationMessage(),
                configured ? Path.GetFullPath(configuredExecutablePath!.Trim()) : null);
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--version");
        try
        {
            if (!process.Start())
            {
                return Failure(executablePath, "Tesseract could not be started.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                           or System.ComponentModel.Win32Exception)
        {
            return Failure(executablePath, exception.Message);
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(versionTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return Failure(
                executablePath,
                $"Tesseract did not report its version within {versionTimeout.TotalSeconds:0} seconds.",
                "timed-out");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var detail = FirstLine(standardError)
                         ?? FirstLine(standardOutput)
                         ?? $"Tesseract exited with code {process.ExitCode}.";
            return Failure(executablePath, detail);
        }

        var version = ParseVersion(standardOutput) ?? ParseVersion(standardError);
        return new DoctorCheck(
            "tool.tesseract",
            "available",
            true,
            false,
            version is null
                ? "Tesseract is available for local screenshot OCR and PII redaction."
                : $"Tesseract {version} is available for local screenshot OCR and PII redaction.",
            executablePath);
    }

    internal static string? ParseVersion(string? output)
    {
        var line = FirstLine(output);
        if (line is null)
        {
            return null;
        }

        const string prefix = "tesseract ";
        return line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? line[prefix.Length..].Trim()
            : line;
    }

    private static string? ResolveExecutablePath(string? configuredExecutablePath, string? inheritedPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredExecutablePath))
        {
            var absolutePath = Path.GetFullPath(configuredExecutablePath.Trim());
            return File.Exists(absolutePath) ? absolutePath : null;
        }

        var executableName = OperatingSystem.IsWindows() ? "tesseract.exe" : "tesseract";
        foreach (var directoryPath in (inheritedPath ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directoryPath.Trim('"'), executableName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static DoctorCheck Failure(string executablePath, string message, string status = "unavailable")
        => new(
            "tool.tesseract",
            status,
            false,
            false,
            $"Tesseract is installed but unavailable for screenshot OCR: {message}",
            executablePath);

    private static string BuildInstallationMessage()
    {
        var installation = OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()
            ? "Install it with 'brew install tesseract'"
            : OperatingSystem.IsWindows()
                ? "Install Tesseract OCR, for example with 'winget install UB-Mannheim.TesseractOCR'"
                : "Install the 'tesseract-ocr' package with your system package manager";
        return $"Tesseract was not found. {installation}, or set {ExecutablePathEnvironmentVariable} to its executable. Screenshot sanitizers will redact whole frames when no other text evidence is available.";
    }

    private static string? FirstLine(string? value)
        => value?
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process already exited.
        }
    }
}
