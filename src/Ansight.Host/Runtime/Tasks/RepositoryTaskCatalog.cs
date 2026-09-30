namespace Ansight.Host.Runtime.Tasks;

/// <summary>
/// Read-only task discovery result for one repository and Ansight App ID.
/// </summary>
public sealed record RepositoryTaskCatalog(
    string RepositoryRootPath,
    string AppId,
    IReadOnlyList<RepositoryTask> Tasks,
    IReadOnlyList<string> Warnings);
