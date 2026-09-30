namespace Ansight.Infrastructure.Teams;

public sealed record KnownTeamProfile(
    string UserId,
    Guid TeamId,
    string Name);
