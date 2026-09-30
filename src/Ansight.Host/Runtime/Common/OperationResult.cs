namespace Ansight.Host.Runtime.Contracts;

public readonly record struct OperationResult(bool IsSuccess, string Message)
{
    public static OperationResult Success(string message) => new(true, message);

    public static OperationResult Failure(string message) => new(false, message);
}
