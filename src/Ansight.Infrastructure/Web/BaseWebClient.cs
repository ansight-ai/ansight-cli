using System.Text;
using System.Text.Json;
using Ansight.Infrastructure.Logging;

namespace Ansight.Infrastructure.Web;

public abstract class BaseWebClient
{
    private readonly ILogger log;
    private readonly ISharedHttpClient sharedHttpClient;
    private readonly IWebClientConfiguration webClientConfiguration;
    private readonly IWebRequestAuthorizer webRequestAuthorizer;

    protected BaseWebClient(
        ISharedHttpClient sharedHttpClient,
        IWebClientConfiguration webClientConfiguration,
        IWebRequestAuthorizer webRequestAuthorizer)
    {
        this.sharedHttpClient = sharedHttpClient ?? throw new ArgumentNullException(nameof(sharedHttpClient));
        this.webClientConfiguration = webClientConfiguration ?? throw new ArgumentNullException(nameof(webClientConfiguration));
        this.webRequestAuthorizer = webRequestAuthorizer ?? throw new ArgumentNullException(nameof(webRequestAuthorizer));
        log = Logger.Create(GetType().Name);
    }

    protected HttpClient HttpClient => sharedHttpClient.HttpClient;

    protected async Task<WebClientResult<T>> GetJsonAsync<T>(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var requestUri = BuildUri(relativePath);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

        return await SendRequestAsync<T>(request, cancellationToken);
    }

    protected async Task<WebClientResult<TResponse>> PostJsonAsync<TRequest, TResponse>(
        string relativePath,
        TRequest payload,
        CancellationToken cancellationToken = default)
    {
        var requestUri = BuildUri(relativePath);
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

        return await SendRequestAsync<TResponse>(request, cancellationToken);
    }

    protected virtual Uri BuildUri(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return webClientConfiguration.BaseUri;
        }

        return new Uri(webClientConfiguration.BaseUri, relativePath.TrimStart('/'));
    }

    private async Task<WebClientResult<T>> SendRequestAsync<T>(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            await webRequestAuthorizer.AuthorizeAsync(request, cancellationToken);

            using var response = await HttpClient.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                log.Warning($"Web request failed for '{request.RequestUri}'. StatusCode: {response.StatusCode}. Message: {content}");
                return WebClientResult<T>.Failure(content, response.StatusCode);
            }

            if (typeof(T) == typeof(string))
            {
                return WebClientResult<T>.Success((T)(object)content, response.StatusCode);
            }

            var value = JsonSerializer.Deserialize<T>(content);
            return WebClientResult<T>.Success(value, response.StatusCode);
        }
        catch (OperationCanceledException)
        {
            return WebClientResult<T>.Failure("The request was cancelled.");
        }
        catch (HttpRequestException ex)
        {
            log.Exception(ex);
            return WebClientResult<T>.Failure(ex.Message, ex.StatusCode);
        }
        catch (JsonException ex)
        {
            log.Exception(ex);
            return WebClientResult<T>.Failure("Unable to parse the server response.");
        }
        catch (Exception ex)
        {
            log.Exception(ex);
            return WebClientResult<T>.Failure("An unknown error occurred while processing the request.");
        }
    }
}
