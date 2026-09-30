
namespace Ansight.Host.Replay;

public sealed record LocalAppRegistrationRequest(
    string AppId,
    string? Name = null,
    string? CodebasePath = null,
    string? PreviousAppId = null);
