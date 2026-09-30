namespace Ansight.Host.Companion;

public sealed record CompanionOperationResult(
    bool IsSuccess,
    string Message,
    CompanionMachine? Machine = null,
    int AffectedConnections = 0);
