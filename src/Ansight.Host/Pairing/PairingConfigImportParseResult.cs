namespace Ansight.Host.Pairing;

public readonly record struct PairingConfigImportParseResult(bool IsSuccess, string Message, PairingConfigImportPayload? Payload)
{
    public static PairingConfigImportParseResult Success(string message, PairingConfigImportPayload payload) =>
        new(true, message, payload);

    public static PairingConfigImportParseResult Failure(string message) => new(false, message, null);
}
