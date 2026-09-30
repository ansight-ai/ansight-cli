using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Ansight.Host.Devices;

internal sealed class IosWebDriverAgentClient : IDisposable
{
    private readonly HttpClient httpClient;
    private readonly Dictionary<string, string> sessionIdsByApplication = new(StringComparer.Ordinal);
    private bool disposed;

    public IosWebDriverAgentClient(IosWebDriverAgentOptions options)
        : this(options, handler: null)
    {
    }

    internal IosWebDriverAgentClient(
        IosWebDriverAgentOptions options,
        HttpMessageHandler? handler)
    {
        ArgumentNullException.ThrowIfNull(options);
        var serverUrl = string.IsNullOrWhiteSpace(options.ServerUrl)
            ? "http://127.0.0.1:8100/"
            : options.ServerUrl.Trim();
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttp
            && endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                "The WebDriverAgent server URL must be an absolute HTTP or HTTPS URL.",
                nameof(options));
        }

        httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        httpClient.BaseAddress = EnsureTrailingSlash(endpoint);
        httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public string Endpoint => httpClient.BaseAddress!.ToString();

    public async Task<IosAccessibilityPageSource> GetPageSourceAsync(
        string applicationIdentifier,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationIdentifier);
        var normalizedApplicationIdentifier = applicationIdentifier.Trim();
        if (!sessionIdsByApplication.TryGetValue(normalizedApplicationIdentifier, out var sessionId))
        {
            sessionId = await CreateSessionAsync(
                normalizedApplicationIdentifier,
                cancellationToken).ConfigureAwait(false);
            sessionIdsByApplication[normalizedApplicationIdentifier] = sessionId;
        }

        var response = await SendAsync(
            HttpMethod.Get,
            $"session/{Escape(sessionId)}/source",
            payload: null,
            cancellationToken).ConfigureAwait(false);
        var source = response["value"] is JsonValue value
                     && value.TryGetValue<string>(out var text)
            ? text
            : null;
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new InvalidOperationException("WebDriverAgent returned no accessibility page source.");
        }

        var viewport = ReadViewport(source);
        return new IosAccessibilityPageSource(source, viewport.Width, viewport.Height);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (var sessionId in sessionIdsByApplication.Values.Distinct(StringComparer.Ordinal))
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var response = httpClient.DeleteAsync(
                        $"session/{Escape(sessionId)}",
                        timeout.Token)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception) when (exception is HttpRequestException
                                               or InvalidOperationException
                                               or TaskCanceledException)
            {
            }
        }

        sessionIdsByApplication.Clear();
        httpClient.Dispose();
    }

    private async Task<string> CreateSessionAsync(
        string applicationIdentifier,
        CancellationToken cancellationToken)
    {
        JsonObject response;
        try
        {
            var desiredCapabilities = new JsonObject
            {
                ["bundleId"] = applicationIdentifier,
                ["arguments"] = new JsonArray(),
                ["environment"] = new JsonObject(),
                ["shouldWaitForQuiescence"] = false
            };
            response = await SendAsync(
                HttpMethod.Post,
                "session",
                new JsonObject
                {
                    ["capabilities"] = new JsonObject
                    {
                        ["alwaysMatch"] = desiredCapabilities.DeepClone(),
                        ["firstMatch"] = new JsonArray(new JsonObject())
                    },
                    ["desiredCapabilities"] = desiredCapabilities
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException(
                $"Could not connect directly to WebDriverAgent at {Endpoint}. Start WDA for the selected iOS target or set ANSIGHT_WDA_SERVER_URL. {exception.Message}",
                exception);
        }

        return ReadString(response["value"], "sessionId")
               ?? ReadString(response, "sessionId")
               ?? throw new InvalidOperationException("WebDriverAgent created no usable session.");
    }

    private async Task<JsonObject> SendAsync(
        HttpMethod method,
        string path,
        JsonObject? payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JsonObject body;
        try
        {
            body = string.IsNullOrWhiteSpace(content)
                ? new JsonObject()
                : JsonNode.Parse(content)?.AsObject() ?? new JsonObject();
        }
        catch (JsonException)
        {
            body = new JsonObject { ["raw"] = content };
        }

        var errorMessage = ReadString(body["value"], "message")
                           ?? ReadString(body["value"], "error");
        if (!response.IsSuccessStatusCode || errorMessage is not null)
        {
            throw new InvalidOperationException(
                errorMessage is null
                    ? $"WebDriverAgent request '{method} {path}' failed with HTTP {(int)response.StatusCode}."
                    : $"WebDriverAgent request '{method} {path}' failed: {errorMessage}");
        }

        return body;
    }

    private static (int Width, int Height) ReadViewport(string source)
    {
        var document = XDocument.Parse(source, LoadOptions.None);
        var elements = document.Descendants()
            .Select(element => new
            {
                X = ReadNumber(element, "x"),
                Y = ReadNumber(element, "y"),
                Width = ReadNumber(element, "width"),
                Height = ReadNumber(element, "height")
            })
            .Where(static bounds => bounds.Width > 0 && bounds.Height > 0)
            .ToArray();
        if (elements.Length == 0)
        {
            throw new InvalidDataException("WebDriverAgent source contains no usable viewport bounds.");
        }

        var width = (int)Math.Ceiling(elements.Max(static bounds => bounds.X + bounds.Width));
        var height = (int)Math.Ceiling(elements.Max(static bounds => bounds.Y + bounds.Height));
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("WebDriverAgent source contains an invalid viewport.");
        }

        return (width, height);
    }

    private static double ReadNumber(XElement element, string name)
        => double.TryParse(
            element.Attribute(name)?.Value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : 0;

    private static string? ReadString(JsonNode? node, string propertyName)
        => node is JsonObject value
           && value[propertyName] is JsonValue property
           && property.TryGetValue<string>(out var text)
            ? text
            : null;

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static Uri EnsureTrailingSlash(Uri endpoint)
        => endpoint.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? endpoint
            : new Uri(endpoint.AbsoluteUri + "/", UriKind.Absolute);
}
