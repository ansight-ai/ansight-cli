using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Ansight.Host.SimulatorAgent.OpenAi.Transport;

internal sealed class OpenAiWebSocketConnection : IOpenAiWebSocketConnection
{
    private const int BufferSize = 16 * 1024;
    private const int MaximumMessageBytes = 16 * 1024 * 1024;
    private readonly ClientWebSocket socket = new();
    private bool disposed;

    public WebSocketState State => socket.State;

    public async Task ConnectAsync(
        Uri endpoint,
        string apiKey,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        socket.Options.SetRequestHeader("Authorization", $"Bearer {apiKey.Trim()}");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendTextAsync(string message, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(message);
        var bytes = Encoding.UTF8.GetBytes(message);
        await socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var buffer = new byte[BufferSize];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidDataException("OpenAI returned a non-text WebSocket message.");
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > MaximumMessageBytes)
            {
                throw new InvalidDataException("OpenAI returned an oversized WebSocket message.");
            }
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(
                    message.GetBuffer(),
                    0,
                    checked((int)message.Length));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Ansight run completed.",
                    closeTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is WebSocketException
                                              or OperationCanceledException)
            {
                // Disposal must not replace the result of the completed agent run.
            }
        }

        socket.Dispose();
    }
}
