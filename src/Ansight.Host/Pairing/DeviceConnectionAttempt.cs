namespace Ansight.Host.Models.Pairing;



public sealed record DeviceConnectionAttempt(
    string InviteId,
    string AppId,
    string DeviceId,
    string DeviceName,
    string AccessToken);
