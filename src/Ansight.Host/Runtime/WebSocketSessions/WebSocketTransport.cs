namespace Ansight.Host.Runtime.WebSocketSessions;

using System.Buffers;
using System.Text;

internal static class WebSocketTransport
{
    internal static async Task SendJsonAsync(WebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonUtil.Compact);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    internal static async Task<WebSocketInboundMessage?> ReceiveMessageAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var payload = new ArrayBufferWriter<byte>();
        WebSocketMessageType? messageType = null;

        while (true)
        {
            var buffer = payload.GetMemory(4096);
            var result = await socket.ReceiveAsync(buffer, cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            messageType ??= result.MessageType;
            if (result.Count > 0)
            {
                payload.Advance(result.Count);
            }

            if (result.EndOfMessage)
            {
                break;
            }
        }

        if (messageType == WebSocketMessageType.Binary)
        {
            return new WebSocketInboundMessage(WebSocketMessageType.Binary, null, payload.WrittenMemory);
        }

        return new WebSocketInboundMessage(WebSocketMessageType.Text, Encoding.UTF8.GetString(payload.WrittenSpan), payload.WrittenMemory);
    }

    internal static bool IsExpectedForcedDisconnectException(Exception ex)
    {
        return ex is WebSocketException
            or ObjectDisposedException
            or IOException
            or InvalidOperationException
               or OperationCanceledException;
    }
}
