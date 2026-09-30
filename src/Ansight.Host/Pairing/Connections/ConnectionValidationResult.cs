namespace Ansight.Host.Pairing.Connections;

internal sealed class ConnectionValidationResult
{
    private ConnectionValidationResult(bool accepted, string reasonCode, string reasonMessage)
    {
        Accepted = accepted;
        ReasonCode = reasonCode;
        ReasonMessage = reasonMessage;
    }

    public bool Accepted { get; }
    public string ReasonCode { get; }
    public string ReasonMessage { get; }

    public static ConnectionValidationResult Accept() => new(true, "Ok", "Connection accepted.");

    public static ConnectionValidationResult Rejected(string reasonCode, string reasonMessage) => new(false, reasonCode, reasonMessage);
}
