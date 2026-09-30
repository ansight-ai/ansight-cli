namespace Ansight.Host.Pairing.Connections;

internal sealed class ConnectResponse
{
    public const string MessageType = "ENROLLMENT_RESULT";

    public string Type { get; set; } = MessageType;
    public int Ver { get; set; } = 2;
    public required string RequestId { get; set; }
    public required bool Accepted { get; set; }
    public required string Reason { get; set; }
    public string? ReasonMessage { get; set; }
    public required string HostId { get; set; }
    public required string HostName { get; set; }
    public string? HostWifiName { get; set; }
    public required string Message { get; set; }
    public int? WebSocketPort { get; set; }
    public string? WebSocketPath { get; set; }
    public string? WebSocketToken { get; set; }
}
