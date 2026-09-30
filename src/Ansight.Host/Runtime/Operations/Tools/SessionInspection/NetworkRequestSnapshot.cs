namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal sealed record NetworkRequestSnapshot(
    string Id,
    string SessionId,
    string AppId,
    NetworkInspectionFilters Filters,
    IReadOnlyList<string> Summaries,
    long CharacterCount,
    DateTimeOffset CreatedUtc);
