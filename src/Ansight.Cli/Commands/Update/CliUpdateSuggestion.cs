using System.Text.Json;

namespace Ansight.Cli.Commands.Update;

internal static class CliUpdateSuggestion
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async System.Threading.Tasks.Task TryWriteAsync(CliOutput output, CancellationToken cancellationToken)
    {
        var installationState = CliInstallationReceiptStore.Load();
        if (installationState.Receipt is null)
        {
            return;
        }

        var cachePath = Path.Combine(
            Path.GetDirectoryName(installationState.ReceiptPath)!,
            "update-check.json");
        if (TryReadRecentCache(cachePath, DateTimeOffset.UtcNow) is not null)
        {
            return;
        }

        try
        {
            using var httpClient = UpdateCommands.CreateHttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(3);
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
            SaveCache(cachePath, status, DateTimeOffset.UtcNow);
            if (status.IsUpdateAvailable)
            {
                output.WriteProgress(
                    $"Ansight CLI {status.LatestVersion} ({status.LatestBuildNumber}) is available on the {status.Channel} channel. Run 'ansight update'.");
            }
        }
        catch (Exception)
        {
            // Update suggestions are best effort and must never break a CLI command.
            SaveFailedCheck(cachePath, installationState.Receipt, DateTimeOffset.UtcNow);
        }
    }

    public static async Task<bool> IsUpdateAvailableAsync(CancellationToken cancellationToken)
    {
        var installationState = CliInstallationReceiptStore.Load();
        if (installationState.Receipt is null)
        {
            return false;
        }

        var cachePath = Path.Combine(
            Path.GetDirectoryName(installationState.ReceiptPath)!,
            "update-check.json");
        if (TryReadRecentCache(cachePath, DateTimeOffset.UtcNow) is { } recentCache)
        {
            return recentCache.IsUpdateAvailable;
        }

        try
        {
            using var httpClient = UpdateCommands.CreateHttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(3);
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
            SaveCache(cachePath, status, DateTimeOffset.UtcNow);
            return status.IsUpdateAvailable;
        }
        catch (Exception)
        {
            SaveFailedCheck(cachePath, installationState.Receipt, DateTimeOffset.UtcNow);
            return false;
        }
    }

    private static CliUpdateSuggestionCache? TryReadRecentCache(
        string cachePath,
        DateTimeOffset now)
    {
        if (!File.Exists(cachePath))
        {
            return null;
        }

        try
        {
            var cache = JsonSerializer.Deserialize<CliUpdateSuggestionCache>(
                File.ReadAllText(cachePath),
                JsonOptions);
            return cache is not null && now - cache.CheckedAtUtc < CheckInterval
                ? cache
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void SaveCache(
        string cachePath,
        CliUpdateStatusOutput status,
        DateTimeOffset checkedAtUtc)
    {
        try
        {
            var cache = new CliUpdateSuggestionCache(
                "ansight.cli.update-check-cache/v1",
                checkedAtUtc,
                status.CurrentVersion,
                status.CurrentBuildNumber,
                status.LatestVersion,
                status.LatestBuildNumber,
                status.Channel,
                status.IsUpdateAvailable);
            File.WriteAllText(
                cachePath,
                JsonSerializer.Serialize(cache, JsonOptions) + Environment.NewLine);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A read-only installation can still use explicit update checks.
        }
    }

    private static void SaveFailedCheck(
        string cachePath,
        CliInstallationReceipt receipt,
        DateTimeOffset checkedAtUtc)
    {
        try
        {
            var cache = new CliUpdateSuggestionCache(
                "ansight.cli.update-check-cache/v1",
                checkedAtUtc,
                receipt.Version,
                receipt.BuildNumber,
                receipt.Version,
                receipt.BuildNumber,
                receipt.Channel,
                false);
            File.WriteAllText(
                cachePath,
                JsonSerializer.Serialize(cache, JsonOptions) + Environment.NewLine);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A read-only installation can still use explicit update checks.
        }
    }
}

internal sealed record CliUpdateSuggestionCache(
    string Schema,
    DateTimeOffset CheckedAtUtc,
    string CurrentVersion,
    long CurrentBuildNumber,
    string LatestVersion,
    long LatestBuildNumber,
    string Channel,
    bool IsUpdateAvailable);
