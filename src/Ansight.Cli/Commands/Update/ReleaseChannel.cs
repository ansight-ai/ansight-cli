namespace Ansight.Cli.Commands.Update;

internal sealed record ReleaseChannel(
    string Name,
    string ReleaseUrl,
    string DownloadBaseUrl)
{
    public const string PublicName = "public";
    public const string PreviewName = "preview";
    public const string PublicReleaseUrl = "https://www.ansight.ai/release.json";
    public const string PreviewReleaseUrl = "https://www.ansight.ai/preview/release.json";
    public const string PublicDownloadBaseUrl = "https://ansightaus.blob.core.windows.net/builds/cli";
    public const string PreviewDownloadBaseUrl = "https://ansightaus.blob.core.windows.net/builds/cli/preview";

    public static ReleaseChannel Resolve(
        string? requestedChannel,
        CliInstallationReceipt? receipt)
    {
        if (!string.IsNullOrWhiteSpace(requestedChannel))
        {
            return FromName(requestedChannel);
        }

        if (receipt is not null
            && TryNormalize(receipt.Channel, out var installedChannel))
        {
            var releaseUrl = string.IsNullOrWhiteSpace(receipt.ReleaseUrl)
                ? GetDefaultReleaseUrl(installedChannel)
                : receipt.ReleaseUrl;
            var downloadBaseUrl = string.IsNullOrWhiteSpace(receipt.DownloadBaseUrl)
                ? GetDefaultDownloadBaseUrl(installedChannel)
                : receipt.DownloadBaseUrl.TrimEnd('/');
            if (IsCanonicalReleaseUrl(installedChannel, releaseUrl)
                && IsLocalFileUrl(downloadBaseUrl))
            {
                downloadBaseUrl = GetDefaultDownloadBaseUrl(installedChannel);
            }

            return new ReleaseChannel(
                installedChannel,
                releaseUrl,
                downloadBaseUrl);
        }

        return FromName(PublicName);
    }

    public static ReleaseChannel FromName(string channel)
    {
        if (!TryNormalize(channel, out var normalized))
        {
            throw new CliUsageException("--channel must be 'public' or 'preview'.");
        }

        return new ReleaseChannel(
            normalized,
            GetDefaultReleaseUrl(normalized),
            GetDefaultDownloadBaseUrl(normalized));
    }

    private static bool TryNormalize(string? channel, out string normalized)
    {
        normalized = channel?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is PublicName or PreviewName;
    }

    private static string GetDefaultReleaseUrl(string channel)
        => channel == PreviewName ? PreviewReleaseUrl : PublicReleaseUrl;

    private static string GetDefaultDownloadBaseUrl(string channel)
        => channel == PreviewName ? PreviewDownloadBaseUrl : PublicDownloadBaseUrl;

    private static bool IsCanonicalReleaseUrl(string channel, string releaseUrl)
        => string.Equals(
            releaseUrl.Trim(),
            GetDefaultReleaseUrl(channel),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalFileUrl(string downloadBaseUrl)
        => Uri.TryCreate(downloadBaseUrl, UriKind.Absolute, out var uri) && uri.IsFile;
}
