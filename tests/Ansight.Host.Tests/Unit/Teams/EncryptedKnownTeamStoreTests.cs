using Ansight.Infrastructure.Teams;

namespace Ansight.Host.Tests.Unit.Teams;

public sealed class EncryptedKnownTeamStoreTests
{
    [Fact]
    public void Save_RestoresSelectedTeamForEachAccount()
    {
        var storage = new InMemoryEncryptedStorage();
        var store = new EncryptedKnownTeamStore(storage);
        var firstTeamId = Guid.NewGuid();
        var secondTeamId = Guid.NewGuid();

        store.Save(new KnownTeamProfile("user-1", firstTeamId, "Team One"));
        store.Save(new KnownTeamProfile("user-2", secondTeamId, "Team Two"));

        var restoredStore = new EncryptedKnownTeamStore(storage);
        Assert.Equal(
            new KnownTeamProfile("user-1", firstTeamId, "Team One"),
            restoredStore.Get("user-1"));
        Assert.Equal(
            new KnownTeamProfile("user-2", secondTeamId, "Team Two"),
            restoredStore.Get("user-2"));
    }

    [Fact]
    public void Save_ReplacesTheSelectedTeamForOneAccount()
    {
        var store = new EncryptedKnownTeamStore(new InMemoryEncryptedStorage());
        var expected = new KnownTeamProfile("user-1", Guid.NewGuid(), "Replacement Team");
        store.Save(new KnownTeamProfile("user-1", Guid.NewGuid(), "Original Team"));

        store.Save(expected);

        Assert.Equal(expected, store.Get("user-1"));
    }

    [Fact]
    public void Remove_LeavesOtherAccountSelectionsIntact()
    {
        var store = new EncryptedKnownTeamStore(new InMemoryEncryptedStorage());
        var remaining = new KnownTeamProfile("user-2", Guid.NewGuid(), "Team Two");
        store.Save(new KnownTeamProfile("user-1", Guid.NewGuid(), "Team One"));
        store.Save(remaining);

        store.Remove("user-1");

        Assert.Null(store.Get("user-1"));
        Assert.Equal(remaining, store.Get("user-2"));
    }
}
