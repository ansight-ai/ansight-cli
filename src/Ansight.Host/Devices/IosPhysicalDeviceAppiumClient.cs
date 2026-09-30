using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Host.Devices;

internal sealed record IosPhysicalDeviceAppiumSessionKey(
    string DeviceIdentifier,
    string ApplicationIdentifier);

internal sealed record IosPhysicalDeviceAppiumSession(
    string SessionId,
    int ViewportWidth,
    int ViewportHeight);

internal sealed record IosAccessibilityPageSource(
    string Source,
    int ViewportWidth,
    int ViewportHeight);

internal sealed class IosPhysicalDeviceAppiumClient : IDisposable
{
    private const string WebDriverElementKey = "element-6066-11e4-a52e-4f735466cecf";
    private readonly HttpClient httpClient;
    private readonly IosPhysicalDeviceAppiumOptions options;
    private readonly ConcurrentDictionary<IosPhysicalDeviceAppiumSessionKey, IosPhysicalDeviceAppiumSession> sessions = [];
    private readonly ConcurrentDictionary<string, IosPhysicalDeviceAppiumSession> monitoringSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> sessionGates = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    public IosPhysicalDeviceAppiumClient(IosPhysicalDeviceAppiumOptions options)
        : this(options, handler: null)
    {
    }

    internal IosPhysicalDeviceAppiumClient(
        IosPhysicalDeviceAppiumOptions options,
        HttpMessageHandler? handler)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        var normalizedServerUrl = string.IsNullOrWhiteSpace(options.ServerUrl)
            ? "http://127.0.0.1:4723/"
            : options.ServerUrl.Trim();
        if (!Uri.TryCreate(normalizedServerUrl, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "The Appium server URL must be an absolute HTTP or HTTPS URL.",
                nameof(options));
        }

        httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        httpClient.BaseAddress = EnsureTrailingSlash(endpoint);
        httpClient.Timeout = TimeSpan.FromSeconds(150);
    }

    public string Endpoint => httpClient.BaseAddress!.ToString();

    public string? GetSessionId(string deviceIdentifier, string applicationIdentifier)
    {
        if (string.IsNullOrWhiteSpace(deviceIdentifier)
            || string.IsNullOrWhiteSpace(applicationIdentifier))
        {
            return null;
        }

        var key = new IosPhysicalDeviceAppiumSessionKey(
            deviceIdentifier.Trim(),
            applicationIdentifier.Trim());
        return sessions.TryGetValue(key, out var session)
            || monitoringSessions.TryGetValue(key.DeviceIdentifier, out session) ? session.SessionId : null;
    }

    public async Task TapAsync(
        string deviceIdentifier,
        string applicationIdentifier,
        double normalizedX,
        double normalizedY,
        CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync(deviceIdentifier, applicationIdentifier, cancellationToken)
            .ConfigureAwait(false);
        var x = ToCoordinate(normalizedX, session.ViewportWidth);
        var y = ToCoordinate(normalizedY, session.ViewportHeight);
        await SendActionsAsync(
            session,
            [
                PointerMove(x, y, 0),
                new JsonObject { ["type"] = "pointerDown", ["button"] = 0 },
                new JsonObject { ["type"] = "pause", ["duration"] = 50 },
                new JsonObject { ["type"] = "pointerUp", ["button"] = 0 }
            ],
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IosAccessibilityPageSource> GetPageSourceAsync(
        string deviceIdentifier,
        string applicationIdentifier,
        CancellationToken cancellationToken)
    {
        return await ReadWithRecoveryAsync(deviceIdentifier, applicationIdentifier, async session =>
        {
            var response = await SendAsync(
                HttpMethod.Get,
                $"session/{Escape(session.SessionId)}/source",
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

            return new IosAccessibilityPageSource(
                source,
                session.ViewportWidth,
                session.ViewportHeight);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task PrepareMonitoringAsync(string deviceIdentifier, string applicationIdentifier,
        CancellationToken cancellationToken)
        => _ = await GetSessionAsync(deviceIdentifier, applicationIdentifier, cancellationToken,
            autoLaunch: false).ConfigureAwait(false);

    public async Task<bool> IsApplicationForegroundAsync(string deviceIdentifier,
        string applicationIdentifier, CancellationToken cancellationToken)
    {
        var response = await ReadWithRecoveryAsync(deviceIdentifier, applicationIdentifier,
            session => SendAsync(HttpMethod.Post, $"session/{Escape(session.SessionId)}/execute/sync",
            new JsonObject
            {
                ["script"] = "mobile: activeAppInfo",
                ["args"] = new JsonArray()
            }, cancellationToken), cancellationToken).ConfigureAwait(false);
        var activeBundleId = ReadString(response["value"], "bundleId");
        if (string.IsNullOrWhiteSpace(activeBundleId))
            throw new InvalidOperationException("WebDriverAgent returned no active application bundle ID.");
        return string.Equals(activeBundleId, applicationIdentifier, StringComparison.Ordinal);
    }

    public async Task<byte[]> GetScreenshotAsync(string deviceIdentifier, string applicationIdentifier,
        CancellationToken cancellationToken)
    {
        var response = await ReadWithRecoveryAsync(deviceIdentifier, applicationIdentifier,
            session => SendAsync(HttpMethod.Get, $"session/{Escape(session.SessionId)}/screenshot", null,
                cancellationToken), cancellationToken).ConfigureAwait(false);
        var encoded = response["value"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(encoded))
            throw new InvalidOperationException("WebDriverAgent returned no physical iOS screenshot.");
        return Convert.FromBase64String(encoded);
    }

    public async Task SwipeAsync(
        string deviceIdentifier,
        string applicationIdentifier,
        double startNormalizedX,
        double startNormalizedY,
        double endNormalizedX,
        double endNormalizedY,
        int durationMilliseconds,
        CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync(deviceIdentifier, applicationIdentifier, cancellationToken)
            .ConfigureAwait(false);
        await SendActionsAsync(
            session,
            [
                PointerMove(
                    ToCoordinate(startNormalizedX, session.ViewportWidth),
                    ToCoordinate(startNormalizedY, session.ViewportHeight),
                    0),
                new JsonObject { ["type"] = "pointerDown", ["button"] = 0 },
                PointerMove(
                    ToCoordinate(endNormalizedX, session.ViewportWidth),
                    ToCoordinate(endNormalizedY, session.ViewportHeight),
                    Math.Clamp(durationMilliseconds, 50, 2_000)),
                new JsonObject { ["type"] = "pointerUp", ["button"] = 0 }
            ],
            cancellationToken).ConfigureAwait(false);
    }

    public async Task TypeTextAsync(
        string deviceIdentifier,
        string applicationIdentifier,
        string text,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync(deviceIdentifier, applicationIdentifier, cancellationToken)
            .ConfigureAwait(false);
        var elementId = await GetActiveElementIdAsync(session, cancellationToken).ConfigureAwait(false);
        if (replaceExisting && elementId is not null)
        {
            await SendAsync(
                HttpMethod.Post,
                $"session/{Escape(session.SessionId)}/element/{Escape(elementId)}/clear",
                new JsonObject(),
                cancellationToken).ConfigureAwait(false);
        }

        if (text.Length == 0)
        {
            if (replaceExisting && elementId is null)
            {
                throw new InvalidOperationException(
                    "Appium could not resolve the focused element required to clear text.");
            }

            return;
        }

        var value = new JsonArray(text.Select(static character => (JsonNode?)character.ToString()).ToArray());
        var payload = new JsonObject
        {
            ["text"] = text,
            ["value"] = value
        };
        var path = elementId is null
            ? $"session/{Escape(session.SessionId)}/keys"
            : $"session/{Escape(session.SessionId)}/element/{Escape(elementId)}/value";
        await SendAsync(HttpMethod.Post, path, payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task PressButtonAsync(
        string deviceIdentifier,
        string applicationIdentifier,
        string button,
        CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync(deviceIdentifier, applicationIdentifier, cancellationToken)
            .ConfigureAwait(false);
        if (string.Equals(button, "back", StringComparison.OrdinalIgnoreCase))
        {
            await SendAsync(
                HttpMethod.Post,
                $"session/{Escape(session.SessionId)}/back",
                new JsonObject(),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var buttonName = button.Trim().ToLowerInvariant() switch
        {
            "home" => "home",
            "lock" or "power" => "lock",
            "volume-up" => "volumeUp",
            "volume-down" => "volumeDown",
            _ => throw new ArgumentException($"Unsupported physical iOS button '{button}'.", nameof(button))
        };
        await SendAsync(
            HttpMethod.Post,
            $"session/{Escape(session.SessionId)}/execute/sync",
            new JsonObject
            {
                ["script"] = "mobile: pressButton",
                ["args"] = new JsonArray(new JsonObject { ["name"] = buttonName })
            },
            cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (var session in sessions.Values.Concat(monitoringSessions.Values).DistinctBy(session => session.SessionId))
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var response = httpClient.DeleteAsync(
                        $"session/{Escape(session.SessionId)}",
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

        sessions.Clear();
        monitoringSessions.Clear();
        httpClient.Dispose();
    }

    public async Task ReleaseSessionAsync(string deviceIdentifier, string applicationIdentifier,
        CancellationToken cancellationToken)
    {
        var key = new IosPhysicalDeviceAppiumSessionKey(deviceIdentifier.Trim(), applicationIdentifier.Trim());
        var gate = sessionGates.GetOrAdd(key.DeviceIdentifier, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!sessions.TryRemove(key, out var session)
                && !monitoringSessions.TryRemove(key.DeviceIdentifier, out session)) return;
            try
            {
                await SendAsync(HttpMethod.Delete, $"session/{Escape(session.SessionId)}", null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
            {
                // The device session may already have ended with the app or Appium server.
            }
        }
        finally { gate.Release(); }
    }

    private async Task<IosPhysicalDeviceAppiumSession> GetSessionAsync(
        string deviceIdentifier,
        string applicationIdentifier,
        CancellationToken cancellationToken,
        bool autoLaunch = true)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationIdentifier);
        var gate = sessionGates.GetOrAdd(deviceIdentifier.Trim(), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CreateOrReuseSessionAsync(deviceIdentifier, applicationIdentifier,
                cancellationToken, autoLaunch).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<IosPhysicalDeviceAppiumSession> CreateOrReuseSessionAsync(
        string deviceIdentifier, string applicationIdentifier, CancellationToken cancellationToken, bool autoLaunch)
    {
        var key = new IosPhysicalDeviceAppiumSessionKey(
            deviceIdentifier.Trim(),
            applicationIdentifier.Trim());
        if (sessions.TryGetValue(key, out var existing))
        {
            return existing;
        }
        if (monitoringSessions.TryGetValue(key.DeviceIdentifier, out existing)) return existing;
        if (!autoLaunch)
        {
            existing = sessions.FirstOrDefault(item => item.Key.DeviceIdentifier == key.DeviceIdentifier).Value;
            if (existing is not null) return existing;
        }

        JsonObject response;
        try
        {
            var capabilities = new JsonObject
            {
                ["platformName"] = "iOS",
                ["appium:automationName"] = "XCUITest",
                ["appium:udid"] = key.DeviceIdentifier,
                ["appium:bundleId"] = key.ApplicationIdentifier,
                ["appium:noReset"] = true,
                ["appium:autoLaunch"] = autoLaunch,
                ["appium:shouldTerminateApp"] = false,
                ["appium:newCommandTimeout"] = 300,
                ["appium:wdaLaunchTimeout"] = 120_000
            };
            AddOptionalCapability(capabilities, "appium:xcodeOrgId", options.XcodeTeamId);
            AddOptionalCapability(
                capabilities,
                "appium:xcodeSigningId",
                options.XcodeSigningIdentity ?? (options.XcodeTeamId is null ? null : "Apple Development"));
            AddOptionalCapability(
                capabilities,
                "appium:updatedWDABundleId",
                options.WebDriverAgentBundleIdentifier);
            AddOptionalCapability(
                capabilities,
                "appium:xcodeConfigFile",
                options.XcodeConfigurationFilePath);

            response = await SendAsync(
                HttpMethod.Post,
                "session",
                new JsonObject
                {
                    ["capabilities"] = new JsonObject
                    {
                        ["alwaysMatch"] = capabilities,
                        ["firstMatch"] = new JsonArray(new JsonObject())
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException(
                $"Could not connect to Appium at {Endpoint}. Start Appium with the XCUITest driver and a signed WebDriverAgent. {exception.Message}",
                exception);
        }

        var sessionId = ReadString(response["value"], "sessionId")
                        ?? ReadString(response, "sessionId")
                        ?? throw new InvalidOperationException("Appium created no usable WebDriver session.");
        var viewport = await ReadViewportAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var session = new IosPhysicalDeviceAppiumSession(sessionId, viewport.Width, viewport.Height);
        if (autoLaunch) sessions[key] = session;
        else monitoringSessions[key.DeviceIdentifier] = session;
        return session;
    }

    private async Task<T> ReadWithRecoveryAsync<T>(string deviceIdentifier, string applicationIdentifier,
        Func<IosPhysicalDeviceAppiumSession, Task<T>> read, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var session = await GetSessionAsync(deviceIdentifier, applicationIdentifier, cancellationToken,
                autoLaunch: false).ConfigureAwait(false);
            try { return await read(session).ConfigureAwait(false); }
            catch (AppiumSessionUnavailableException) when (attempt == 0)
            {
                // Retry one read with a fresh connection. Never replay app input or launch the app.
            }
        }
    }

    private void ForgetSession(string sessionId)
    {
        foreach (var item in sessions.Where(item => item.Value.SessionId == sessionId))
            sessions.TryRemove(item);
        foreach (var item in monitoringSessions.Where(item => item.Value.SessionId == sessionId))
            monitoringSessions.TryRemove(item);
    }

    private async Task<IosPhysicalDeviceViewport> ReadViewportAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            HttpMethod.Get,
            $"session/{Escape(sessionId)}/window/rect",
            payload: null,
            cancellationToken).ConfigureAwait(false);
        var value = response["value"] as JsonObject;
        var width = ReadInteger(value, "width");
        var height = ReadInteger(value, "height");
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("Appium returned no usable physical-device viewport.");
        }

        return new IosPhysicalDeviceViewport(width, height);
    }

    private async Task<string?> GetActiveElementIdAsync(
        IosPhysicalDeviceAppiumSession session,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            HttpMethod.Get,
            $"session/{Escape(session.SessionId)}/element/active",
            payload: null,
            cancellationToken).ConfigureAwait(false);
        var value = response["value"] as JsonObject;
        return ReadString(value, WebDriverElementKey) ?? ReadString(value, "ELEMENT");
    }

    private async Task SendActionsAsync(
        IosPhysicalDeviceAppiumSession session,
        IReadOnlyList<JsonObject> pointerActions,
        CancellationToken cancellationToken)
    {
        await SendAsync(
            HttpMethod.Post,
            $"session/{Escape(session.SessionId)}/actions",
            new JsonObject
            {
                ["actions"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "pointer",
                        ["id"] = "ansight-finger",
                        ["parameters"] = new JsonObject { ["pointerType"] = "touch" },
                        ["actions"] = new JsonArray(pointerActions.Select(static action => (JsonNode?)action).ToArray())
                    })
            },
            cancellationToken).ConfigureAwait(false);
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

        var isWebDriverError = IsWebDriverError(body, out var errorMessage);
        if (!response.IsSuccessStatusCode || isWebDriverError)
        {
            var message = string.IsNullOrWhiteSpace(errorMessage)
                    ? $"Appium request '{method} {path}' failed with HTTP {(int)response.StatusCode}."
                    : $"Appium request '{method} {path}' failed: {errorMessage}";
            var error = ReadString(body["value"], "error");
            if (path.StartsWith("session/", StringComparison.Ordinal)
                && (error == "invalid session id"
                    || response.StatusCode == System.Net.HttpStatusCode.NotFound && !isWebDriverError))
            {
                ForgetSession(Uri.UnescapeDataString(path.Split('/')[1]));
                throw new AppiumSessionUnavailableException(message);
            }
            throw new InvalidOperationException(message);
        }

        return body;
    }

    private static bool IsWebDriverError(JsonObject body, out string? message)
    {
        message = null;
        if (body["value"] is not JsonObject value)
        {
            return false;
        }

        var error = ReadString(value, "error");
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        message = ReadString(value, "message") ?? error;
        return true;
    }

    private static JsonObject PointerMove(int x, int y, int duration)
        => new()
        {
            ["type"] = "pointerMove",
            ["duration"] = duration,
            ["x"] = x,
            ["y"] = y,
            ["origin"] = "viewport"
        };

    private static void AddOptionalCapability(JsonObject capabilities, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            capabilities[name] = value.Trim();
        }
    }

    private static string? ReadString(JsonNode? node, string propertyName)
        => node is JsonObject value
           && value[propertyName] is JsonValue property
           && property.TryGetValue<string>(out var text)
            ? text
            : null;

    private static int ReadInteger(JsonObject? value, string propertyName)
        => value?[propertyName] is JsonValue property
           && property.TryGetValue<int>(out var number)
            ? number
            : 0;

    private static int ToCoordinate(double normalizedCoordinate, int viewportSize)
        => (int)Math.Round(Math.Clamp(normalizedCoordinate, 0d, 1d) * Math.Max(0, viewportSize - 1));

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static Uri EnsureTrailingSlash(Uri endpoint)
        => endpoint.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? endpoint
            : new Uri(endpoint.AbsoluteUri + "/", UriKind.Absolute);

    private sealed record IosPhysicalDeviceViewport(int Width, int Height);

    private sealed class AppiumSessionUnavailableException(string message) : InvalidOperationException(message);
}
