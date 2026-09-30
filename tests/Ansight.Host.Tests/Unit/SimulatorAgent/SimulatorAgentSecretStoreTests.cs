using Ansight.Infrastructure.Security;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed class SimulatorAgentSecretStoreTests
{
    [Fact]
    public void Secrets_AreVersionedPerAppAndResolvedOnlyForDeclaredRunAliases()
    {
        var storage = new InMemoryEncryptedStorage();
        var store = new SecretStore(storage);

        var first = store.Set("com.example.app", "login.password", "first-value");
        var second = store.Set("com.example.app", "login.password", "second-value");
        store.Set("com.other.app", "login.password", "other-value");

        var listed = Assert.Single(store.List("COM.EXAMPLE.APP"));
        Assert.Equal("login.password", listed.Alias);
        Assert.NotEqual(first.VersionId, second.VersionId);
        Assert.Equal(second.VersionId, listed.VersionId);

        var access = store.CreateRunAccess("com.example.app", ["login.password"]);
        Assert.Equal("second-value", access.Resolve("login.password"));
        Assert.Throws<InvalidOperationException>(() =>
            store.CreateRunAccess("com.example.app", ["missing.secret"]));

        Assert.True(store.Remove("com.example.app", "login.password"));
        Assert.Empty(store.List("com.example.app"));
        Assert.Single(store.List("com.other.app"));
    }

    [Fact]
    public void EnvironmentVariables_AreUsedAsFallbackWithStoredValuesTakingPrecedence()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TEST_USER_PASSWORD"] = "environment-value"
        };
        var requestedVariables = new List<string>();
        var store = new SecretStore(
            new InMemoryEncryptedStorage(),
            name =>
            {
                requestedVariables.Add(name);
                return environment.GetValueOrDefault(name);
            });

        var environmentAccess = store.CreateRunAccess(
            "com.example.app",
            ["TEST_USER_PASSWORD"]);

        Assert.Equal("environment-value", environmentAccess.Resolve("TEST_USER_PASSWORD"));
        var environmentMetadata = Assert.Single(environmentAccess.Secrets.Values);
        Assert.Equal("environment", environmentMetadata.VersionId);
        Assert.Equal(["TEST_USER_PASSWORD"], requestedVariables);

        var storedMetadata = store.Set(
            "com.example.app",
            "TEST_USER_PASSWORD",
            "stored-value");
        requestedVariables.Clear();

        var storedAccess = store.CreateRunAccess(
            "com.example.app",
            ["TEST_USER_PASSWORD"]);

        Assert.Equal("stored-value", storedAccess.Resolve("TEST_USER_PASSWORD"));
        Assert.Equal(storedMetadata.VersionId, Assert.Single(storedAccess.Secrets.Values).VersionId);
        Assert.Empty(requestedVariables);
    }

    [Fact]
    public void EnvironmentVariables_MustMatchADeclaredAliasAndContainAValue()
    {
        var store = new SecretStore(
            new InMemoryEncryptedStorage(),
            name => name switch
            {
                "DECLARED_SECRET" => "available-value",
                "EMPTY_SECRET" => string.Empty,
                _ => null
            });

        Assert.NotNull(store.GetMetadata("com.example.app", "DECLARED_SECRET"));
        Assert.Null(store.GetMetadata("com.example.app", "EMPTY_SECRET"));
        Assert.Throws<InvalidOperationException>(() =>
            store.CreateRunAccess("com.example.app", ["MISSING_SECRET"]));
    }

    [Fact]
    public void EnvironmentVariables_RejectOversizedValues()
    {
        var store = new SecretStore(
            new InMemoryEncryptedStorage(),
            _ => new string('x', 16_385));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            store.CreateRunAccess("com.example.app", ["OVERSIZED_SECRET"]));

        Assert.Contains("16,384", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("space alias")]
    [InlineData("/path")]
    [InlineData("")]
    public void Set_RejectsInvalidAliases(string alias)
    {
        var store = new SecretStore(new InMemoryEncryptedStorage());

        Assert.ThrowsAny<ArgumentException>(() =>
            store.Set("com.example.app", alias, "value"));
    }
}
