namespace Ansight.Host.Models.Pairing;



public sealed record DeviceConnectionAuthorization(
    bool Accepted,
    string ReasonCode,
    string ReasonMessage,
    PairingClientGrant? Grant,
    bool IsNewRegistration)
{
    public static DeviceConnectionAuthorization Accept(
        PairingClientGrant grant,
        bool isNewRegistration)
        => new(true, "Ok", "Device registered.", grant, isNewRegistration);

    public static DeviceConnectionAuthorization Reject(string reasonCode, string reasonMessage)
        => new(false, reasonCode, reasonMessage, null, false);
}
