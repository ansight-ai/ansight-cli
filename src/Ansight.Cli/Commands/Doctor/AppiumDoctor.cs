using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace Ansight.Cli.Commands.Doctor;

internal static class AppiumDoctor
{
    internal const string DefaultServerUrl = "http://127.0.0.1:4723/";

    public static async Task<IReadOnlyList<DoctorCheck>> CheckAsync(
        string? appiumPath,
        string? configuredServerUrl,
        CancellationToken cancellationToken)
    {
        var xcuiTestCheckTask = CheckXcuiTestDriverAsync(appiumPath, cancellationToken);
        var serverCheckTask = CheckServerAsync(configuredServerUrl, cancellationToken);
        return await Task.WhenAll(xcuiTestCheckTask, serverCheckTask).ConfigureAwait(false);
    }

    internal static async Task<DoctorCheck> CheckXcuiTestDriverAsync(
        string? appiumPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(appiumPath))
        {
            return new DoctorCheck(
                "device.ios.appium.xcuitest",
                "not-checked",
                false,
                false,
                "The XCUITest driver cannot be checked because 'appium' was not found on PATH. Install Appium, then run 'appium driver install xcuitest'.",
                null);
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = appiumPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("driver");
        process.StartInfo.ArgumentList.Add("list");
        process.StartInfo.ArgumentList.Add("--installed");
        process.StartInfo.ArgumentList.Add("--json");

        try
        {
            if (!process.Start())
            {
                return DriverInspectionFailure(
                    appiumPath,
                    "The Appium process could not be started.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                           or System.ComponentModel.Win32Exception)
        {
            return DriverInspectionFailure(appiumPath, exception.Message);
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return DriverInspectionFailure(
                appiumPath,
                "Timed out while listing installed Appium drivers.",
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
            var detail = FirstNonEmpty(standardError, standardOutput)
                         ?? $"Appium exited with code {process.ExitCode}.";
            return DriverInspectionFailure(appiumPath, Summarize(detail));
        }

        if (!ContainsXcuiTestDriver(standardOutput))
        {
            return new DoctorCheck(
                "device.ios.appium.xcuitest",
                "not-installed",
                false,
                false,
                "The Appium XCUITest driver is not installed. Run 'appium driver install xcuitest'.",
                appiumPath);
        }

        return new DoctorCheck(
            "device.ios.appium.xcuitest",
            "available",
            true,
            false,
            "The Appium XCUITest driver is installed.",
            appiumPath);
    }

    internal static async Task<DoctorCheck> CheckServerAsync(
        string? configuredServerUrl,
        CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        return await CheckServerAsync(
                configuredServerUrl,
                httpClient,
                TimeSpan.FromSeconds(2),
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<DoctorCheck> CheckServerAsync(
        string? configuredServerUrl,
        HttpClient httpClient,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        var serverUrl = FirstNonEmpty(configuredServerUrl) ?? DefaultServerUrl;
        if (!TryCreateStatusEndpoint(serverUrl, out var statusEndpoint))
        {
            return new DoctorCheck(
                "device.ios.appium.server",
                "invalid-configuration",
                false,
                false,
                "ANSIGHT_APPIUM_SERVER_URL must be an absolute HTTP or HTTPS URL.",
                serverUrl);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            using var response = await httpClient.GetAsync(statusEndpoint, timeoutSource.Token)
                .ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(timeoutSource.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return ServerFailure(
                    serverUrl,
                    $"Appium returned HTTP {(int)response.StatusCode} ({response.StatusCode}) from its status endpoint.");
            }

            var readiness = ParseServerReadiness(content);
            if (readiness.IsReady == false)
            {
                return ServerFailure(
                    serverUrl,
                    string.IsNullOrWhiteSpace(readiness.Message)
                        ? "The Appium server responded but reported that it is not ready."
                        : $"The Appium server reported that it is not ready: {readiness.Message}",
                    "not-ready");
            }

            return new DoctorCheck(
                "device.ios.appium.server",
                "available",
                true,
                false,
                readiness.IsReady == true
                    ? "The Appium server status endpoint is reachable and ready."
                    : "The Appium server status endpoint is reachable.",
                serverUrl);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ServerFailure(
                serverUrl,
                $"Timed out while connecting to the Appium server at {serverUrl}.",
                "timed-out");
        }
        catch (HttpRequestException exception)
        {
            return ServerFailure(
                serverUrl,
                $"Could not connect to the Appium server at {serverUrl}: {exception.Message}");
        }
        catch (JsonException exception)
        {
            return ServerFailure(
                serverUrl,
                $"The Appium status endpoint returned invalid JSON: {exception.Message}",
                "invalid-response");
        }
    }

    internal static bool ContainsXcuiTestDriver(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            return ContainsXcuiTestDriver(document.RootElement);
        }
        catch (JsonException)
        {
            return output.Contains("xcuitest", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static AppiumServerReadiness ParseServerReadiness(string content)
    {
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        var value = root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("value", out var nestedValue)
            ? nestedValue
            : root;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return new AppiumServerReadiness(null, null);
        }

        bool? isReady = value.TryGetProperty("ready", out var readyValue)
                        && readyValue.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? readyValue.GetBoolean()
            : null;
        var message = value.TryGetProperty("message", out var messageValue)
                      && messageValue.ValueKind == JsonValueKind.String
            ? messageValue.GetString()
            : null;
        return new AppiumServerReadiness(isReady, message);
    }

    private static bool ContainsXcuiTestDriver(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Contains("xcuitest", StringComparison.OrdinalIgnoreCase)
                        || ContainsXcuiTestDriver(property.Value))
                    {
                        return true;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ContainsXcuiTestDriver(item))
                    {
                        return true;
                    }
                }

                break;
            case JsonValueKind.String:
                return element.GetString()?.Contains(
                    "xcuitest",
                    StringComparison.OrdinalIgnoreCase) == true;
        }

        return false;
    }

    private static bool TryCreateStatusEndpoint(string serverUrl, out Uri? statusEndpoint)
    {
        statusEndpoint = null;
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var normalizedUrl = serverUrl.EndsWith("/", StringComparison.Ordinal)
            ? serverUrl
            : serverUrl + "/";
        statusEndpoint = new Uri(new Uri(normalizedUrl, UriKind.Absolute), "status");
        return true;
    }

    private static DoctorCheck DriverInspectionFailure(
        string appiumPath,
        string detail,
        string status = "unavailable")
        => new(
            "device.ios.appium.xcuitest",
            status,
            false,
            false,
            $"Could not inspect installed Appium drivers. {detail}",
            appiumPath);

    private static DoctorCheck ServerFailure(
        string serverUrl,
        string message,
        string status = "unavailable")
        => new(
            "device.ios.appium.server",
            status,
            false,
            false,
            message,
            serverUrl);

    private static string Summarize(string value)
    {
        const int maximumLength = 400;
        var normalized = string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength] + "...";
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                           or System.ComponentModel.Win32Exception
                                           or NotSupportedException)
        {
            // The process exited while cancellation was being handled.
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
