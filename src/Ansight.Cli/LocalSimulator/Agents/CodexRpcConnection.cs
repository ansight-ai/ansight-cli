using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;

namespace Ansight.Cli.LocalSimulator;

internal sealed class CodexRpcConnection : ICodexRpcConnection
{
    private readonly ClientWebSocket socket;
    private readonly HttpMessageInvoker invoker;
    private int requestId;

    private CodexRpcConnection(ClientWebSocket socket, HttpMessageInvoker invoker)
    {
        this.socket = socket;
        this.invoker = invoker;
    }

    public static async Task<ICodexRpcConnection> ConnectAsync(string socketPath, CancellationToken cancellationToken)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, token) =>
            {
                var transport = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

                try
                {
                    await transport.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), token).ConfigureAwait(false);

                    return new NetworkStream(transport, ownsSocket: true);
                }
                catch
                {
                    transport.Dispose();
                    throw;
                }
            }
        };
        var invoker = new HttpMessageInvoker(handler);
        var socket = new ClientWebSocket();
        var connection = new CodexRpcConnection(socket, invoker);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(new Uri("ws://localhost/"), invoker, timeout.Token).ConfigureAwait(false);
            await connection.CallAsync("initialize", new
            {
                clientInfo = new { name = "ansight_companion", title = "Ansight Companion", version = "1.0.0" },
                capabilities = new { experimentalApi = true }
            }, timeout.Token).ConfigureAwait(false);
            await connection.SendAsync(new { method = "initialized", @params = new { } }, timeout.Token).ConfigureAwait(false);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var id = ++requestId;
        await SendAsync(new { id, method, @params = parameters }, timeout.Token).ConfigureAwait(false);

        while (true)
        {
            using var document = await ReadAsync(timeout.Token).ConfigureAwait(false);
            var message = document.RootElement;
            var matchesResponse = !message.TryGetProperty("method", out _)
                && message.TryGetProperty("id", out var responseId)
                && responseId.ValueKind == JsonValueKind.Number
                && responseId.TryGetInt32(out var value)
                && value == id;

            if (!matchesResponse)
            {
                continue;
            }

            if (message.TryGetProperty("error", out var error))
            {
                throw new CodexRpcException(error.GetProperty("message").GetString() ?? "Codex rejected this request.");
            }

            return message.GetProperty("result").Clone();
        }
    }

    private Task SendAsync(object value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);

        return socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
    }

    private async Task<JsonDocument> ReadAsync(CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        var buffer = new byte[16384];

        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new IOException("The Codex connection closed. Reconnect to Codex and try again.");
            }

            if (message.Length + result.Count > 32 * 1024 * 1024)
            {
                throw new InvalidDataException("Codex returned an oversized response.");
            }

            message.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
            {
                return JsonDocument.Parse(message.ToArray());
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        socket.Dispose();
        invoker.Dispose();

        return ValueTask.CompletedTask;
    }
}
