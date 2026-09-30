namespace Ansight.Host.Runtime.WebSocketSessions;



internal static class WebSocketAcceptor
{
    private const int MaximumHeaderBytes = 16 * 1024;
    private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();

    public static async Task<WebSocketConnection> AcceptAsync(
        TcpListener listener,
        string expectedPath,
        string expectedToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedToken);

        while (true)
        {
            var tcpClient = await listener.AcceptTcpClientAsync(cancellationToken);
            NetworkStream? networkStream = null;
            try
            {
                networkStream = tcpClient.GetStream();
                var request = await ReadUpgradeRequestAsync(networkStream, cancellationToken);
                if (!string.Equals(request.Path, expectedPath, StringComparison.Ordinal))
                {
                    await WriteFailureAsync(networkStream, "404 Not Found", cancellationToken);
                    throw new InvalidDataException("WebSocket request path did not match the session offer.");
                }

                if (!string.Equals(request.Token, expectedToken, StringComparison.Ordinal))
                {
                    await WriteFailureAsync(networkStream, "401 Unauthorized", cancellationToken);
                    throw new InvalidDataException("WebSocket request token did not match the session offer.");
                }

                var acceptValue = Convert.ToBase64String(
                    SHA1.HashData(
                        Encoding.ASCII.GetBytes(
                            request.WebSocketKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                var response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 101 Switching Protocols\r\n" +
                    "Upgrade: websocket\r\n" +
                    "Connection: Upgrade\r\n" +
                    $"Sec-WebSocket-Accept: {acceptValue}\r\n\r\n");
                await networkStream.WriteAsync(response, cancellationToken);
                await networkStream.FlushAsync(cancellationToken);

                var webSocket = WebSocket.CreateFromStream(
                    networkStream,
                    new WebSocketCreationOptions
                    {
                        IsServer = true,
                        KeepAliveInterval = TimeSpan.FromSeconds(30)
                    });
                return new WebSocketConnection(tcpClient, networkStream, webSocket);
            }
            catch (InvalidDataException)
            {
                networkStream?.Dispose();
                tcpClient.Dispose();
            }
            catch
            {
                networkStream?.Dispose();
                tcpClient.Dispose();
                throw;
            }
        }
    }

    private static async Task<WebSocketUpgradeRequest> ReadUpgradeRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var singleByte = new byte[1];
        while (buffer.Length < MaximumHeaderBytes)
        {
            var count = await stream.ReadAsync(singleByte, cancellationToken);
            if (count == 0)
            {
                throw new EndOfStreamException("WebSocket client closed during the HTTP upgrade.");
            }

            buffer.WriteByte(singleByte[0]);
            if (EndsWith(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)), HeaderTerminator))
            {
                break;
            }
        }

        if (buffer.Length >= MaximumHeaderBytes)
        {
            throw new InvalidDataException("WebSocket upgrade headers exceeded the maximum size.");
        }

        var headerText = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines.FirstOrDefault()?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine is not { Length: 3 }
            || !string.Equals(requestLine[0], "GET", StringComparison.Ordinal)
            || !string.Equals(requestLine[2], "HTTP/1.1", StringComparison.Ordinal))
        {
            throw new InvalidDataException("WebSocket upgrade request line is invalid.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        if (!headers.TryGetValue("Upgrade", out var upgrade)
            || !string.Equals(upgrade, "websocket", StringComparison.OrdinalIgnoreCase)
            || !headers.TryGetValue("Connection", out var connection)
            || !connection.Split(',').Any(value => string.Equals(value.Trim(), "Upgrade", StringComparison.OrdinalIgnoreCase))
            || !headers.TryGetValue("Sec-WebSocket-Version", out var version)
            || !string.Equals(version, "13", StringComparison.Ordinal)
            || !headers.TryGetValue("Sec-WebSocket-Key", out var key)
            || !IsValidWebSocketKey(key))
        {
            throw new InvalidDataException("WebSocket upgrade headers are invalid.");
        }

        var requestTarget = requestLine[1];
        var querySeparator = requestTarget.IndexOf('?');
        var path = querySeparator < 0 ? requestTarget : requestTarget[..querySeparator];
        var query = querySeparator < 0 ? string.Empty : requestTarget[(querySeparator + 1)..];
        return new WebSocketUpgradeRequest(path, ReadToken(query), key);
    }

    private static string? ReadToken(string query)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var encodedName = separator < 0 ? pair : pair[..separator];
            if (!string.Equals(
                    Uri.UnescapeDataString(encodedName.Replace("+", " ", StringComparison.Ordinal)),
                    "token",
                    StringComparison.Ordinal))
            {
                continue;
            }

            var encodedValue = separator < 0 ? string.Empty : pair[(separator + 1)..];
            return Uri.UnescapeDataString(encodedValue.Replace("+", " ", StringComparison.Ordinal));
        }

        return null;
    }

    private static bool IsValidWebSocketKey(string value)
    {
        try
        {
            return Convert.FromBase64String(value).Length == 16;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool EndsWith(ReadOnlySpan<byte> value, ReadOnlySpan<byte> suffix)
        => value.Length >= suffix.Length && value[^suffix.Length..].SequenceEqual(suffix);

    private static async Task WriteFailureAsync(Stream stream, string status, CancellationToken cancellationToken)
    {
        var response = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
        await stream.WriteAsync(response, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
