using System.Text.Json;

namespace Ansight.Cli.Commands.Update;

internal sealed class CliUpdateService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient httpClient;
    private readonly CliReleaseIdentity identity;
    private readonly CliInstallationState installationState;
    private readonly string rid;

    public CliUpdateService(
        HttpClient httpClient,
        CliReleaseIdentity identity,
        CliInstallationState installationState,
        string rid)
    {
        this.httpClient = httpClient;
        this.identity = identity;
        this.installationState = installationState;
        this.rid = rid;
    }

    public async Task<CliUpdateStatusOutput> CheckAsync(
        string? requestedChannel,
        string? requestedVersion,
        long? requestedBuildNumber,
        CancellationToken cancellationToken)
    {
        if ((requestedVersion is null) != (requestedBuildNumber is null))
        {
            throw new CliUsageException(
                "Use --version and --build-number together when selecting an exact CLI build.");
        }

        var channel = ReleaseChannel.Resolve(requestedChannel, installationState.Receipt);
        string latestVersion;
        long latestBuildNumber;
        string? summary;
        DateTimeOffset? publishedAtUtc;
        if (requestedVersion is not null && requestedBuildNumber is not null)
        {
            latestVersion = requestedVersion.Trim();
            latestBuildNumber = requestedBuildNumber.Value;
            summary = null;
            publishedAtUtc = null;
        }
        else
        {
            using var response = await httpClient.GetAsync(channel.ReleaseUrl, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync<CliReleaseFeed>(
                              content,
                              JsonOptions,
                              cancellationToken)
                          .ConfigureAwait(false)
                          ?? throw new InvalidOperationException(
                              $"The {channel.Name} release feed is empty.");
            if (!string.IsNullOrWhiteSpace(release.Channel)
                && !string.Equals(release.Channel, channel.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The {channel.Name} release feed identified itself as '{release.Channel}'.");
            }

            if (!string.IsNullOrWhiteSpace(release.Product)
                && !string.Equals(release.Product, "cli", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The release feed describes '{release.Product}', not the Ansight CLI.");
            }

            latestVersion = !string.IsNullOrWhiteSpace(release.CliVersion)
                ? release.CliVersion.Trim()
                : release.Version?.Trim() ?? string.Empty;
            latestBuildNumber = release.CliBuildNumber is > 0
                ? release.CliBuildNumber.Value
                : release.BuildNumber;
            summary = !string.IsNullOrWhiteSpace(release.CliSummary)
                ? release.CliSummary
                : release.Summary;
            publishedAtUtc = release.CliPublishedAt ?? release.PublishedAt;
        }

        ValidateTarget(latestVersion, latestBuildNumber);
        var comparison = CompareBuilds(
            identity.Version,
            identity.BuildNumber,
            latestVersion,
            latestBuildNumber);
        var installedChannel = installationState.Receipt?.Channel;
        var isChannelChange = !string.IsNullOrWhiteSpace(requestedChannel)
                              && !string.Equals(
                                  installedChannel,
                                  channel.Name,
                                  StringComparison.OrdinalIgnoreCase);
        var archiveName = $"ansight-cli-{rid}-{latestVersion}-{latestBuildNumber}.tar.gz";
        var archiveUrl = $"{channel.DownloadBaseUrl.TrimEnd('/')}/{rid}/{latestVersion}/{latestBuildNumber}/{archiveName}";

        return new CliUpdateStatusOutput(
            "ansight.cli.update-status/v1",
            identity.Version,
            identity.BuildNumber,
            latestVersion,
            latestBuildNumber,
            channel.Name,
            rid,
            comparison < 0,
            comparison != 0,
            isChannelChange,
            channel.ReleaseUrl,
            channel.DownloadBaseUrl,
            archiveUrl,
            summary,
            publishedAtUtc);
    }

    internal static int CompareBuilds(
        string leftVersion,
        long leftBuildNumber,
        string rightVersion,
        long rightBuildNumber)
    {
        if (!CliReleaseVersion.TryParse(leftVersion, out var left)
            || !CliReleaseVersion.TryParse(rightVersion, out var right))
        {
            var fallback = string.Compare(leftVersion, rightVersion, StringComparison.OrdinalIgnoreCase);
            return fallback != 0 ? fallback : leftBuildNumber.CompareTo(rightBuildNumber);
        }

        var versionComparison = left!.CompareTo(right);
        return versionComparison != 0
            ? versionComparison
            : leftBuildNumber.CompareTo(rightBuildNumber);
    }

    private static void ValidateTarget(string version, long buildNumber)
    {
        if (!CliReleaseVersion.TryParse(version, out _))
        {
            throw new InvalidOperationException(
                $"The release feed contains an invalid CLI version '{version}'.");
        }

        if (buildNumber <= 0)
        {
            throw new InvalidOperationException(
                "The release feed does not contain a positive integer cliBuildNumber or buildNumber.");
        }
    }
}
