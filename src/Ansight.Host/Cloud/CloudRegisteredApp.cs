using Ansight.Host.Trends;

namespace Ansight.Host.Cloud;

public sealed record CloudRegisteredApp(
    Guid Id,
    Guid TeamId,
    string AppId,
    string Name,
    string? Platform,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
