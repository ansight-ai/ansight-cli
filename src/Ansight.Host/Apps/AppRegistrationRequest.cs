namespace Ansight.Host.Apps;

public sealed record AppRegistrationRequest(
    string AppId,
    string? Name = null,
    string? CodebasePath = null);
