namespace Ansight.Host.Apps;

public sealed record AppOperationResult(
    bool IsSuccess,
    string Message,
    AppDescriptor? App = null);
