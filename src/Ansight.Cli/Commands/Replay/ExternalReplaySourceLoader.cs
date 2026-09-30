using System.Net.Http.Headers;
using Ansight.Host;

namespace Ansight.Cli.Commands.Replay;

internal sealed class ExternalReplaySourceLoader : IDisposable
{
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;

    public ExternalReplaySourceLoader(HttpClient? httpClient = null)
    {
        this.httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
        ownsHttpClient = httpClient is null;
    }

    public async Task<ReplayPlan> LoadAsync(
        string provider,
        string source,
        string? appId,
        CliArguments arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (File.Exists(source))
        {
            return await ExternalReplayPlanBuilder.BuildFromFileAsync(
                    provider,
                    source,
                    appId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (Path.GetExtension(source).ToLowerInvariant() is ".json" or ".jsonl" or ".ndjson")
        {
            throw new CliUsageException($"Replay export file '{Path.GetFullPath(source)}' was not found.");
        }

        var normalizedProvider = provider.Trim().ToLowerInvariant();
        var tokenEnvironmentVariable = arguments.GetOption("token-env")
                                       ?? (normalizedProvider == "sentry"
                                           ? "SENTRY_AUTH_TOKEN"
                                           : "POSTHOG_PERSONAL_API_KEY");
        var token = CliCommandContext.Current?.SecretValue
                    ?? Environment.GetEnvironmentVariable(tokenEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new CliUsageException(
                $"Replay source '{source}' is not a local file and {tokenEnvironmentVariable} is not set. "
                + "Export the replay to JSON, or set the provider API token environment variable.");
        }

        var requestUri = normalizedProvider switch
        {
            "sentry" => BuildSentryUri(source, arguments),
            "posthog" or "post-hog" => BuildPostHogUri(source, arguments),
            _ => throw new CliUsageException(
                $"Unsupported replay provider '{provider}'. Expected sentry or posthog.")
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new CliUsageException(
                $"{NormalizeDisplayName(normalizedProvider)} replay download failed with HTTP {(int)response.StatusCode} "
                + $"({response.ReasonPhrase}). Verify the replay identifier, project, host, and token scope.");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (content.Length > 128 * 1024 * 1024)
        {
            throw new CliUsageException(
                $"{NormalizeDisplayName(normalizedProvider)} replay download exceeds the 128 MiB import limit.");
        }
        var temporaryPath = Path.Combine(
            Path.GetTempPath(),
            $"ansight-{normalizedProvider}-replay-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);
            var plan = await ExternalReplayPlanBuilder.BuildFromFileAsync(
                    normalizedProvider,
                    temporaryPath,
                    appId,
                    cancellationToken)
                .ConfigureAwait(false);
            return plan with
            {
                SourceId = source.Trim()
            };
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void Dispose()
    {
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }
    }

    internal static Uri BuildSentryUri(string replayId, CliArguments arguments)
    {
        var organization = arguments.RequireOption("organization");
        var project = arguments.RequireOption("project");
        var host = NormalizeHost(arguments.GetOption("host") ?? "https://sentry.io");
        return new Uri(
            host,
            $"api/0/projects/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}"
            + $"/replays/{Uri.EscapeDataString(NormalizeReplayId(replayId))}/recording-segments/?per_page=100");
    }

    internal static Uri BuildPostHogUri(string recordingId, CliArguments arguments)
    {
        var project = arguments.RequireOption("project");
        var host = NormalizeHost(arguments.GetOption("host") ?? "https://us.posthog.com");
        return new Uri(
            host,
            $"api/projects/{Uri.EscapeDataString(project)}/session_recordings/"
            + $"{Uri.EscapeDataString(NormalizeReplayId(recordingId))}/snapshots");
    }

    private static Uri NormalizeHost(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new CliUsageException("--host must be an absolute HTTP or HTTPS URL.");
        }

        return uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? uri
            : new Uri(uri.AbsoluteUri + "/");
    }

    private static string NormalizeReplayId(string value)
    {
        var normalized = value.Trim();
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return normalized;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var replayIndex = Array.FindLastIndex(
            segments,
            segment => segment.Equals("replays", StringComparison.OrdinalIgnoreCase)
                       || segment.Equals("replay", StringComparison.OrdinalIgnoreCase)
                       || segment.Equals("session_recordings", StringComparison.OrdinalIgnoreCase));
        return replayIndex >= 0 && replayIndex + 1 < segments.Length
            ? segments[replayIndex + 1]
            : segments.LastOrDefault() ?? normalized;
    }

    private static string NormalizeDisplayName(string provider)
        => provider == "posthog" ? "PostHog" : "Sentry";
}
