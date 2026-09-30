using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Ansight.Host.AppGraphs;
using Ansight.Host.Files;
using Ansight.Host.Trends;
using Ansight.Host.Runtime.BinaryTransfers;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Operations.Tools.UiAutomation;
using Ansight.Infrastructure.Preferences;

namespace Ansight.Host.Explorer;

internal sealed partial class ExplorerServer : IAsyncDisposable
{
    internal static string? NormalizeRequestedPath(string? requestedPath)
    {
        if (requestedPath is null)
        {
            return null;
        }

        var path = requestedPath.Trim();
        if (path == "/")
        {
            return path;
        }

        var segment = path.Trim('/');
        if (segment.Length is 0 or > 256 || segment is "." or ".." || segment.Any(static character => !IsUrlSafePathCharacter(character)))
        {
            throw new InvalidOperationException("Explorer path must be '/' or one URL-safe segment containing only ASCII letters, digits, '.', '_', '~', or '-'.");
        }

        return $"/{segment}/";
    }

    private static string ResolveReplayPath(string? requestedPath) => NormalizeRequestedPath(requestedPath) ?? $"/{CryptoUtil.CreateBase64UrlRandom(24)}/";
    private static bool IsUrlSafePathCharacter(char character) => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '.' or '_' or '~';
    private async Task<bool> TryHandleTransportGetAsync(string route, HttpListenerRequest request, HttpListenerResponse response, bool isHead, CancellationToken cancellationToken)
    {
        switch (route)
        {
            case "":
            case "index.html":
                await WriteBytesAsync(response, htmlAsset.Value.Content, "text/html; charset=utf-8", HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
                return true;
            case "session-replay.css":
                await WriteReplayAssetAsync(request, response, cssAsset.Value, "text/css; charset=utf-8", false, isHead, cancellationToken).ConfigureAwait(false);
                return true;
            case "session-replay.js":
                await WriteReplayAssetAsync(request, response, javascriptAsset.Value, "text/javascript; charset=utf-8", false, isHead, cancellationToken).ConfigureAwait(false);
                return true;
            case "api/health" when isExplorer:
                await WriteJsonAsync(response, await runtime.Health.InspectAsync(cancellationToken).ConfigureAwait(false), HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
                return true;
            case "api/health/logs" when isExplorer:
                await WriteJsonAsync(response, ListHostLogs(), HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
                return true;
            case "favicon.ico":
                response.StatusCode = (int)HttpStatusCode.NoContent;
                response.Close();
                return true;
        }

        return false;
    }

    private async Task<bool> TryHandleTransportPostAsync(string route, HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken)
    {
        switch (route)
        {
            case "api/analytics/events":
                {
                    var body = await ReadJsonAsync<LocalWebAnalyticsRequest>(request, cancellationToken).ConfigureAwait(false);
                    await analytics.TrackAsync(body, isExplorer, cancellationToken).ConfigureAwait(false);
                    response.StatusCode = (int)HttpStatusCode.Accepted;
                    response.Close();
                    return true;
                }

            default:
                break;
        }

        return false;
    }

    internal static async Task WriteServerSentEventAsync(HttpListenerResponse response, string value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteImageFileAsync(HttpListenerResponse response, string path, bool isHead, CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = ResolveImageContentType(file.Extension);
        response.ContentLength64 = file.Length;
        if (!isHead)
        {
            await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 64 * 1024, useAsync: true);
            await stream.CopyToAsync(response.OutputStream, cancellationToken).ConfigureAwait(false);
        }

        response.Close();
    }

    internal static async Task<T> ReadJsonAsync<T>(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength64 > MaximumRequestBodyBytes)
        {
            throw new InvalidDataException("The request body is too large.");
        }

        await using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = await request.InputStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            if (output.Length + count > MaximumRequestBodyBytes)
            {
                throw new InvalidDataException("The request body is too large.");
            }

            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }

        return JsonSerializer.Deserialize<T>(output.GetBuffer().AsSpan(0, checked((int)output.Length)), jsonOptions) ?? throw new JsonException("The JSON request body is required.");
    }

    private static void EnsureJsonRequest(HttpListenerRequest request)
    {
        if (request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
        {
            throw new InvalidDataException("POST requests must use application/json.");
        }
    }

    private static int ReadNonNegativeQueryInteger(HttpListenerRequest request, string name)
    {
        var value = request.QueryString[name];
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        if (!int.TryParse(value, out var parsed) || parsed < 0)
        {
            throw new InvalidDataException($"Query parameter '{name}' must be a non-negative integer.");
        }

        return parsed;
    }

    private static int ReadBoundedPositiveQueryInteger(HttpListenerRequest request, string name, int defaultValue, int maximumValue)
    {
        var value = request.QueryString[name];
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!int.TryParse(value, out var parsed) || parsed < 1 || parsed > maximumValue)
        {
            throw new InvalidDataException($"Query parameter '{name}' must be between 1 and {maximumValue:N0}.");
        }

        return parsed;
    }

    private static string? ReadOptionalString(JsonObject body, string name)
    {
        return body[name] is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
    }

    private static bool TryReadAppToolResult(RuntimeAppToolResponse response, out JsonObject result, out string errorMessage)
    {
        result = new JsonObject();
        errorMessage = response.Message;
        if (!response.Success || response.Envelope?.Payload is not JsonObject payload)
        {
            return false;
        }

        if (payload["success"] is JsonValue successValue && successValue.TryGetValue<bool>(out var success) && !success)
        {
            errorMessage = ReadOptionalString(payload, "message") ?? response.Message;
            return false;
        }

        if (payload["result"] is not JsonObject resultObject)
        {
            errorMessage = ReadOptionalString(payload, "message") ?? "The app returned no file content.";
            return false;
        }

        result = resultObject;
        return true;
    }

    private static long ReadOptionalInt64(JsonObject body, string name, long fallback)
    {
        if (body[name] is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue<long>(out var result))
        {
            return result;
        }

        if (value.TryGetValue<int>(out var intResult))
        {
            return intResult;
        }

        return long.TryParse(value.ToString(), out var parsed) ? parsed : fallback;
    }

    private static bool ReadOptionalBoolean(JsonObject body, string name)
    {
        return body[name] is JsonValue value && value.TryGetValue<bool>(out var result) && result;
    }

    private static int ReadOptionalInt32(JsonObject body, string name, int fallback)
    {
        if (body[name] is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue<int>(out var result))
        {
            return result;
        }

        return value.TryGetValue<long>(out var longResult) ? checked((int)Math.Clamp(longResult, int.MinValue, int.MaxValue)) : fallback;
    }

    internal static Task WriteJsonAsync<T>(HttpListenerResponse response, T value, HttpStatusCode statusCode, bool isHead, CancellationToken cancellationToken) => WriteBytesAsync(response, JsonSerializer.SerializeToUtf8Bytes(value, jsonOptions), "application/json; charset=utf-8", statusCode, isHead, cancellationToken);
    private static Task WriteTextAsync(HttpListenerResponse response, string value, HttpStatusCode statusCode, bool isHead, CancellationToken cancellationToken) => WriteBytesAsync(response, Encoding.UTF8.GetBytes(value), "text/plain; charset=utf-8", statusCode, isHead, cancellationToken);
    private static async Task WriteBytesAsync(HttpListenerResponse response, byte[] value, string contentType, HttpStatusCode statusCode, bool isHead, CancellationToken cancellationToken)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = contentType;
        response.ContentLength64 = value.Length;
        if (!isHead)
        {
            await response.OutputStream.WriteAsync(value, cancellationToken).ConfigureAwait(false);
        }

        response.Close();
    }

    private static async Task WriteReplayAssetAsync(HttpListenerRequest request, HttpListenerResponse response, CachedReplayAsset asset, string contentType, bool isImmutable, bool isHead, CancellationToken cancellationToken)
    {
        response.Headers["Cache-Control"] = isImmutable ? "private, max-age=31536000, immutable" : "private, max-age=0, must-revalidate";
        response.Headers["ETag"] = asset.EntityTag;
        if (string.Equals(request.Headers["If-None-Match"], asset.EntityTag, StringComparison.Ordinal))
        {
            response.StatusCode = (int)HttpStatusCode.NotModified;
            response.Close();
            return;
        }

        await WriteBytesAsync(response, asset.Content, contentType, HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
    }

    private static void ApplySecurityHeaders(HttpListenerResponse response)
    {
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data: blob: https://*.mapbox.com; script-src 'self' 'wasm-unsafe-eval' https://api.mapbox.com; style-src 'self' 'unsafe-inline' https://api.mapbox.com; connect-src 'self' blob: https://api.mapbox.com https://events.mapbox.com; worker-src 'self' blob:; child-src blob:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
        response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["X-Frame-Options"] = "DENY";
    }

    private static string ResolveImageContentType(string extension) => extension.TrimStart('.').ToLowerInvariant() switch
    {
        "png" => "image/png",
        "jpg" or "jpeg" => "image/jpeg",
        "webp" => "image/webp",
        "gif" => "image/gif",
        _ => "application/octet-stream"
    };
    private static bool TryResolveReplayAssetContentType(string fileName, out string contentType)
    {
        contentType = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".wasm" => "application/wasm",
            ".ttf" => "font/ttf",
            ".woff2" => "font/woff2",
            ".woff" => "font/woff",
            ".png" => "image/png",
            _ => string.Empty
        };
        return contentType.Length > 0;
    }

    private static byte[] LoadAsset(string fileName)
    {
        var assembly = typeof(ExplorerServer).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourcePrefix + fileName) ?? throw new InvalidOperationException($"Embedded replay asset '{fileName}' was not found.");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static CachedReplayAsset LoadCachedAsset(string fileName)
    {
        var content = LoadAsset(fileName);
        var hash = SHA256.HashData(content);
        return new CachedReplayAsset(content, $"\"{Convert.ToHexString(hash)}\"");
    }

    private static bool TryLoadAsset(string fileName, out CachedReplayAsset value)
    {
        try
        {
            value = additionalAssets.GetOrAdd(fileName, static name => LoadCachedAsset(name));
            return true;
        }
        catch (InvalidOperationException)
        {
            value = default;
            return false;
        }
    }
}
