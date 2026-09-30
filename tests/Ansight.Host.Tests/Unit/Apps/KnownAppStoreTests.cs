namespace Ansight.Host.Tests.Unit.Apps;

public sealed class KnownAppStoreTests
{
    [Fact]
    public void EnsureKnown_CreatesAndUpdatesExistingApp()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new KnownAppStore(environment.ApplicationPaths);
        var firstSeen = DateTimeOffset.Parse("2026-03-20T00:00:00Z");
        var lastSeen = firstSeen.AddHours(4);

        store.EnsureKnown("app-1", "Example App", "glyph-a", "/tmp/codebase", firstSeen);
        store.EnsureKnown("app-1", "Ignored Rename", "glyph-b", "/tmp/updated", lastSeen);

        var app = Assert.Single(store.GetSnapshot());
        Assert.Equal("app-1", app.AppId);
        Assert.Equal("Example App", app.Name);
        Assert.Equal("glyph-a", app.IconGlyph);
        Assert.Equal("/tmp/updated", app.CodebasePath);
        Assert.True(app.RepositoryAutomationsEnabled);
        Assert.Equal(firstSeen, app.FirstSeenUtc);
        Assert.Equal(lastSeen, app.LastSeenUtc);
    }

    [Fact]
    public void EnsureKnown_PreservesExplicitDisableForUnchangedWorkspace()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new KnownAppStore(environment.ApplicationPaths);
        var firstSeen = DateTimeOffset.Parse("2026-03-20T00:00:00Z");
        store.EnsureKnown("app-1", "Example App", "glyph-a", "/tmp/codebase", firstSeen);
        var app = Assert.Single(store.GetSnapshot());
        app.RepositoryAutomationsEnabled = false;
        store.Upsert(app);

        store.EnsureKnown(
            "app-1",
            "Example App",
            "glyph-a",
            "/tmp/codebase",
            firstSeen.AddHours(1));

        Assert.False(Assert.Single(store.GetSnapshot()).RepositoryAutomationsEnabled);
    }

    [Fact]
    public void Upsert_PersistsAcrossStoreInstances()
    {
        using var environment = new TestSupport.TestEnvironment();
        var firstStore = new KnownAppStore(environment.ApplicationPaths);
        firstStore.Upsert(new KnownAppDefinition
        {
            AppId = "app-1",
            Name = "Example App",
            IconGlyph = "glyph-a",
            CodebasePath = "/tmp/codebase",
            FirstSeenUtc = DateTimeOffset.Parse("2026-03-20T00:00:00Z"),
            LastSeenUtc = DateTimeOffset.Parse("2026-03-20T01:00:00Z")
        });

        var secondStore = new KnownAppStore(environment.ApplicationPaths);

        var app = Assert.Single(secondStore.GetSnapshot());
        Assert.Equal("Example App", app.Name);
        Assert.Equal("/tmp/codebase", app.CodebasePath);
    }

    [Fact]
    public void Changed_FiresForPersistedMutations()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new KnownAppStore(environment.ApplicationPaths);
        var changedCount = 0;
        store.Changed += (_, _) => changedCount++;

        var firstSeen = DateTimeOffset.Parse("2026-03-20T00:00:00Z");
        store.EnsureKnown("app-1", "Example App", "glyph-a", seenAtUtc: firstSeen);
        store.EnsureKnown("app-1", "Example App", "glyph-a", seenAtUtc: firstSeen);
        store.Upsert(new KnownAppDefinition
        {
            AppId = "app-1",
            Name = "Updated App",
            IconGlyph = "glyph-b",
            FirstSeenUtc = firstSeen,
            LastSeenUtc = firstSeen.AddHours(1)
        });
        Assert.True(store.Remove("app-1"));

        Assert.Equal(3, changedCount);
    }
}
