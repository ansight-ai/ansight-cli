using System.Net.Http.Headers;

namespace Ansight.Host.SimulatorAgent.OpenAi.Transport;

internal sealed class LocalOpenAiTransport : IModelExecutionTransport
{
    private static readonly HttpClient sharedClient = new()
    {
        Timeout = TimeSpan.FromMinutes(3)
    };

    private readonly string apiKey;
    private readonly HttpClient httpClient;

    public LocalOpenAiTransport(string apiKey, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        this.apiKey = apiKey;
        this.httpClient = httpClient ?? sharedClient;
    }

    public string Description => "local-openai-key";
    public bool SupportsWebSockets => true;

    public ValueTask<string> ResolveAccessKeyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(apiKey);
    }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var endpoint = request.RequestUri;
        var isOpenAiEndpoint = endpoint is not null
            && endpoint.IsAbsoluteUri
            && endpoint.Scheme == Uri.UriSchemeHttps
            && endpoint.Host == "api.openai.com"
            && endpoint.IsDefaultPort;

        if (!isOpenAiEndpoint)
        {
            throw new InvalidOperationException("The local OpenAI API key can only be used with api.openai.com over HTTPS.");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    public string DescribeFailure(int statusCode, string responseContent)
    {
        return statusCode switch
        {
            401 => "OpenAI rejected the saved API key. Replace it in Runner settings and retry.",
            403 => "The saved OpenAI API key cannot access the requested model or operation.",
            429 => "The OpenAI project reached its rate or usage limit. Check the project limits and retry.",
            _ => $"OpenAI request failed with HTTP {statusCode}."
        };
    }
}
