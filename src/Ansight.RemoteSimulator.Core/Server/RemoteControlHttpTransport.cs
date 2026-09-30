using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Ansight.RemoteSimulator.Core.Server;

internal static class RemoteControlHttpTransport
{
    private const int MaximumHeaderBytes = 16 * 1024;
    private const int MaximumRequestBodyBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<HttpRequestData?> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var rented = ArrayPool<byte>.Shared.Rent(MaximumHeaderBytes);
        try
        {
            var headerLength = 0;
            while (headerLength < MaximumHeaderBytes)
            {
                var read = await stream.ReadAsync(rented.AsMemory(headerLength, 1), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return null;
                }

                headerLength += read;
                if (headerLength >= 4
                    && rented[headerLength - 4] == '\r'
                    && rented[headerLength - 3] == '\n'
                    && rented[headerLength - 2] == '\r'
                    && rented[headerLength - 1] == '\n')
                {
                    break;
                }
            }

            if (headerLength == MaximumHeaderBytes)
            {
                throw new InvalidOperationException("HTTP request headers are too large.");
            }

            var headerText = Encoding.ASCII.GetString(rented, 0, headerLength);
            var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
            {
                return null;
            }

            var requestParts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (requestParts.Length != 3)
            {
                throw new InvalidOperationException("Malformed HTTP request line.");
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var separator = line.IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }

                var name = line[..separator].Trim();
                headers[name] = line[(separator + 1)..].Trim();
            }

            var contentLength = headers.TryGetValue("Content-Length", out var contentLengthValue)
                ? int.Parse(contentLengthValue)
                : 0;

            if (contentLength is < 0 or > MaximumRequestBodyBytes)
            {
                throw new InvalidOperationException("HTTP request body is too large.");
            }

            var body = new byte[contentLength];
            var offset = 0;
            while (offset < body.Length)
            {
                var read = await stream.ReadAsync(body.AsMemory(offset), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new InvalidOperationException("HTTP request body ended unexpectedly.");
                }

                offset += read;
            }

            return new HttpRequestData(
                requestParts[0].ToUpperInvariant(),
                requestParts[1],
                headers,
                body);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = separator < 0 ? pair : pair[..separator];
            var value = separator < 0 ? string.Empty : pair[(separator + 1)..];
            values[Uri.UnescapeDataString(name.Replace('+', ' '))] =
                Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return values;
    }

    public static Task WriteJsonAsync<T>(
        NetworkStream stream,
        HttpStatusCode statusCode,
        T value,
        CancellationToken cancellationToken)
        => WriteResponseAsync(
            stream,
            statusCode,
            "application/json; charset=utf-8",
            JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            cancellationToken,
            "Cache-Control: no-store\r\n");

    public static Task WriteTextAsync(
        NetworkStream stream,
        HttpStatusCode statusCode,
        string value,
        string contentType,
        CancellationToken cancellationToken)
        => WriteResponseAsync(stream, statusCode, contentType, Encoding.UTF8.GetBytes(value), cancellationToken);

    public static async Task WriteResponseAsync(
        NetworkStream stream,
        HttpStatusCode statusCode,
        string contentType,
        byte[] body,
        CancellationToken cancellationToken,
        string extraHeaders = "")
    {
        var reason = statusCode switch
        {
            HttpStatusCode.OK => "OK",
            HttpStatusCode.Accepted => "Accepted",
            HttpStatusCode.BadRequest => "Bad Request",
            HttpStatusCode.Unauthorized => "Unauthorized",
            HttpStatusCode.Forbidden => "Forbidden",
            HttpStatusCode.NotFound => "Not Found",
            HttpStatusCode.Conflict => "Conflict",
            HttpStatusCode.RequestEntityTooLarge => "Content Too Large",
            HttpStatusCode.ServiceUnavailable => "Service Unavailable",
            _ => "Internal Server Error",
        };
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {(int)statusCode} {reason}\r\n"
            + $"Content-Type: {contentType}\r\n"
            + $"Content-Length: {body.Length}\r\n"
            + "Connection: close\r\n"
            + "X-Content-Type-Options: nosniff\r\n"
            + "Referrer-Policy: no-referrer\r\n"
            + extraHeaders
            + "\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
