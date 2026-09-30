namespace Ansight.Host.Pairing;

public readonly record struct PairingConfigDeserializeResult(bool IsSuccess, string Message, CachedPairingConfig? Config)
{
    public static PairingConfigDeserializeResult Success(string message, CachedPairingConfig config) => new(true, message, config);

    public static PairingConfigDeserializeResult Failure(string message) => new(false, message, null);
}
