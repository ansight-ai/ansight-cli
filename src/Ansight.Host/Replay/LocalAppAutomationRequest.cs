
namespace Ansight.Host.Replay;

public sealed record LocalAppAutomationRequest(
    string AppId,
    string? RepositoryRootPath = null);
