namespace Ansight.Host.Runtime.WebSocketSessions;



internal sealed class WebSocketConnection : IAsyncDisposable
{
    private readonly TcpClient tcpClient;
    private readonly NetworkStream networkStream;

    public WebSocketConnection(
        TcpClient tcpClient,
        NetworkStream networkStream,
        WebSocket webSocket)
    {
        this.tcpClient = tcpClient;
        this.networkStream = networkStream;
        WebSocket = webSocket;
    }

    public WebSocket WebSocket { get; }

    public async ValueTask DisposeAsync()
    {
        WebSocket.Dispose();
        await networkStream.DisposeAsync();
        tcpClient.Dispose();
    }
}
