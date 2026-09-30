namespace Ansight.Host.Runtime.WebSocketSessions;

internal sealed class WebSocketSessionInfo
{
    public WebSocketSessionInfo(int port, string path, string token)
    {
        Port = port;
        Path = path;
        Token = token;
    }

    public int ProtocolVersion => 2;
    public int Port { get; }
    public string Path { get; }
    public string Token { get; }
}
