using System.Text.Json.Nodes;
using Ansight.Cli.Input;
using Ansight.Host;
using Ansight.Host.SimulatorAgent.Secrets;
using Ansight.Infrastructure.Security;

namespace Ansight.Cli.Tests.Commands.Input;

public sealed class SecretTextInputTests
{
    [Fact]
    public async Task ResolvesTheSelectedAppsStoredSecretAndReturnsOnlyStatus()
    {
        const string value = "  test-password-with-spaces  ";
        var store = new SecretStore(new MemoryStorage(), _ => null);
        store.Set("com.example.app", "login.password", value);
        store.Set("com.other.app", "login.password", "wrong-app-password");
        var supplied = new JsonObject { ["appId"] = "com.example.app", ["secretAlias"] = "login.password", ["automationId"] = "Password" };
        var result = await SecretTextInput.TypeUiAsync(supplied,
            _ => new SecretTextInput.Target("session-1", "com.example.app"),
            (app, alias) => store.CreateRunAccess(app, [alias]).Resolve(alias)!,
            (operation, input, _) =>
            {
                Assert.Equal(UiAutomationOperation.Type, operation);
                Assert.Equal(value, input["value"]!.GetValue<string>());
                Assert.Equal("session-1", input["sessionId"]!.GetValue<string>());
                Assert.False(input.ContainsKey("appId"));
                Assert.False(input.ContainsKey("secretAlias"));
                Assert.False(input["includeScreenshot"]!.GetValue<bool>());
                return Task.FromResult(new UiAutomationResult(operation, true, value,
                    new JsonObject { ["target"] = new JsonObject { ["value"] = value } }));
            }, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.DoesNotContain(value, result.Message + result.Payload.ToJsonString());
        Assert.Equal("login.password", result.Payload["secretAlias"]!.GetValue<string>());
        Assert.False(supplied.ContainsKey("value"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedInputNeverEchoesBackendPayloadsOrExceptions(bool throws)
    {
        const string value = "secret-that-must-not-escape";
        var result = await SecretTextInput.TypeUiAsync(new JsonObject { ["secretAlias"] = "password" },
            _ => new SecretTextInput.Target("session-1", "com.example.app"), (_, _) => value,
            (operation, _, _) => throws
                ? throw new InvalidOperationException(value)
                : Task.FromResult(new UiAutomationResult(operation, false, value, new JsonObject { ["error"] = value })),
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.DoesNotContain(value, result.Message + result.Payload.ToJsonString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidTargetOrMissingSecretDoesNotSendAnyInput(bool invalidTarget)
    {
        var called = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => SecretTextInput.TypeUiAsync(
            new JsonObject { ["secretAlias"] = "password" },
            _ => invalidTarget ? throw new InvalidOperationException("Ambiguous target")
                : new SecretTextInput.Target("session-1", "com.example.app"),
            (_, _) => throw new InvalidOperationException("Missing secret"),
            (_, _, _) => { called = true; throw new InvalidOperationException("Must not type"); },
            CancellationToken.None));
        Assert.False(called);
    }

    [Fact]
    public async Task QualifiedReferenceCannotUseAnotherAppsSession()
    {
        await Assert.ThrowsAsync<CliUsageException>(() => SecretTextInput.TypeUiAsync(
            new JsonObject { ["secretAlias"] = "password", ["secretAppId"] = "com.example.app" },
            _ => new SecretTextInput.Target("session-other", "com.other.app"),
            (_, _) => throw new InvalidOperationException("Must not read another app's secret"),
            (_, _, _) => throw new InvalidOperationException("Must not type"), CancellationToken.None));
    }

    private sealed class MemoryStorage : IEncryptedStorage
    {
        private readonly Dictionary<string, string> values = [];
        public string? Get(string key) => values.GetValueOrDefault(key);
        public void Set(string key, string? value) { if (value is null) values.Remove(key); else values[key] = value; }
        public void Remove(string key) => values.Remove(key);
        public void Clear() => values.Clear();
    }
}
