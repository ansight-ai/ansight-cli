namespace Ansight.Infrastructure.Teams;

public interface IKnownTeamStore
{
    KnownTeamProfile? Get(string userId);

    void Save(KnownTeamProfile profile);

    void Remove(string userId);
}
