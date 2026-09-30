using System.Text.Json.Nodes;
using Ansight.Infrastructure.Security;

namespace Ansight.Cli.Tests.Commands.Session;

public sealed class SessionNormalizationCommandsTests
{
    [Fact]
    public async Task Normalize_MissingSession_ReturnsStructuredFailure()
    {
        using var directory = TestDirectory.Create();
        var storePath = Path.Combine(directory.Path, "secure-storage.json");
        var keyPath = Path.Combine(directory.Path, "secure-storage.key");
        FileEncryptionKeyProvider.CreateKeyFile(keyPath);
        var arguments = CliArguments.Parse(
        [
            "session",
            "normalize",
            "missing-session",
            "--json",
            "--data-dir",
            directory.Path,
            "--secret-store-file",
            storePath,
            "--secret-key-file",
            keyPath
        ]);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await CliApplication.RunParsedAsync(
            arguments,
            new CliOutput(arguments.IsJson, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);

        Assert.Equal(CliExitCodes.Failure, exitCode);
        var payload = JsonNode.Parse(standardOutput.ToString())!.AsObject();
        Assert.Equal("ansight.session-normalization/v1", payload["schemaVersion"]!.GetValue<string>());
        Assert.Equal("missing-session", payload["sessionId"]!.GetValue<string>());
        Assert.False(payload["result"]!["isSuccess"]!.GetValue<bool>());
        Assert.Equal(0, payload["result"]!["removedItemCount"]!.GetValue<int>());
        Assert.Equal(string.Empty, standardError.ToString());
    }
}
