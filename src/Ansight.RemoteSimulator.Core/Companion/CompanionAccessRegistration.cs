namespace Ansight.RemoteSimulator.Core.Companion;

internal sealed record CompanionAccessRegistration(
    Guid LeaseId,
    RemoteCompanionDevice Device,
    DateTimeOffset ConnectedAtUtc,
    DateTimeOffset ExpiresAtUtc);
