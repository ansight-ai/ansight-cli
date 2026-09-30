namespace Ansight.Host.Pairing.Connections;

internal sealed class EnrollmentConnectRequest
{
    public const string MessageType = "ENROLLMENT_CONNECT";
    public const string InviteMode = "invite";
    public const string LocalMode = "local";

    public string Type { get; set; } = MessageType;
    public int Ver { get; set; } = 2;
    public string EnrollmentMode { get; set; } = InviteMode;
    public required string RequestId { get; set; }
    public required string InviteId { get; set; }
    public required string AppId { get; set; }
    public required string DeviceId { get; set; }
    public required string DeviceName { get; set; }
    public required string AccessToken { get; set; }
    public string? ProcessSessionId { get; set; }
}
