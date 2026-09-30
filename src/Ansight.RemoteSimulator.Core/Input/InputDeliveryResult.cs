namespace Ansight.RemoteSimulator.Core.Input;

public sealed record InputDeliveryResult(bool IsSuccess, string Backend, string Message)
{
    public static InputDeliveryResult Success(string backend, string message = "Input delivered.")
        => new(true, backend, message);

    public static InputDeliveryResult Failure(string backend, string message)
        => new(false, backend, message);
}
