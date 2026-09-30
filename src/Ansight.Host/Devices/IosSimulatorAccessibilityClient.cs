using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Host.Devices;

internal sealed class IosSimulatorAccessibilityClient
{
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(10);
    private readonly string? executablePath;

    public IosSimulatorAccessibilityClient(string? configuredExecutablePath)
    {
        executablePath = ResolveExecutablePath(configuredExecutablePath);
    }

    public bool IsAvailable => executablePath is not null;

    public string AvailabilityMessage => executablePath is null
        ? "AXe was not found. Install AXe or set ANSIGHT_AXE_PATH to enable direct CoreSimulator accessibility capture."
        : $"CoreSimulator accessibility capture is available through AXe at '{executablePath}'.";

    public async Task<JsonNode> DescribeUiAsync(
        string deviceIdentifier,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceIdentifier);
        if (executablePath is null)
        {
            throw new InvalidOperationException(AvailabilityMessage);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("describe-ui");
        startInfo.ArgumentList.Add("--udid");
        startInfo.ArgumentList.Add(deviceIdentifier.Trim());

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CaptureTimeout);
        try
        {
            using var process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("Could not start AXe.");
            var standardOutputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var standardErrorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var standardOutput = await standardOutputTask.ConfigureAwait(false);
            var standardError = (await standardErrorTask.ConfigureAwait(false)).Trim();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(standardError)
                        ? $"AXe describe-ui failed with exit code {process.ExitCode}."
                        : $"AXe describe-ui failed: {standardError}");
            }

            try
            {
                return JsonNode.Parse(standardOutput)
                       ?? throw new InvalidDataException("AXe describe-ui returned an empty JSON document.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    "AXe describe-ui returned invalid JSON.",
                    exception);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"AXe describe-ui exceeded the {CaptureTimeout.TotalSeconds:0}-second capture limit.");
        }
    }

    internal static string? ResolveExecutablePath(string? configuredExecutablePath)
    {
        var configured = string.IsNullOrWhiteSpace(configuredExecutablePath)
            ? Environment.GetEnvironmentVariable("ANSIGHT_AXE_PATH")
            : configuredExecutablePath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var absolutePath = Path.GetFullPath(configured.Trim());
            return File.Exists(absolutePath) ? absolutePath : null;
        }

        foreach (var directoryPath in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directoryPath, "axe");
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        foreach (var candidate in new[]
                 {
                     "/opt/homebrew/bin/axe",
                     "/usr/local/bin/axe"
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
