using System.Text.Json.Nodes;
using Ansight.Infrastructure.Security;

namespace Ansight.Cli.Tests.Commands.Pairing;

public sealed class PairingCommandsTests
{
    [Fact]
    public async Task IssueAndGetJsonDoNotExposeEnrollmentSecret()
    {
        using var directory = TestDirectory.Create();
        var storePath = Path.Combine(directory.Path, "secure-storage.json");
        var keyPath = Path.Combine(directory.Path, "secure-storage.key");
        var appId = $"com.example.cli.{Guid.NewGuid():N}";
        FileEncryptionKeyProvider.CreateKeyFile(keyPath);
        var issueOutput = await RunAsync(
            [
                "pairing",
                "issue",
                appId,
                "--json",
                "--data-dir",
                directory.Path,
                "--secret-store-file",
                storePath,
                "--secret-key-file",
                keyPath
            ]);
        var issuePayload = JsonNode.Parse(issueOutput)!.AsObject();
        var inviteFilePath = issuePayload["inviteFilePath"]!.GetValue<string>();

        try
        {
            var inviteJson = JsonNode.Parse(File.ReadAllText(inviteFilePath))!.AsObject();
            var enrollmentSecret = inviteJson["enrollment"]!["accessToken"]!.GetValue<string>();
            var inviteId = issuePayload["invite"]!["inviteId"]!.GetValue<string>();

            Assert.DoesNotContain(enrollmentSecret, issueOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("inviteJson", issueOutput, StringComparison.OrdinalIgnoreCase);

            var detailOutput = await RunAsync(
                [
                    "pairing",
                    "get",
                    inviteId,
                    "--json",
                    "--data-dir",
                    directory.Path,
                    "--secret-store-file",
                    storePath,
                    "--secret-key-file",
                    keyPath
                ]);

            Assert.DoesNotContain(enrollmentSecret, detailOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("inviteJson", detailOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(inviteFilePath);
        }
    }

    [Fact]
    public async Task IssueWithCodeReturnsManualPairingCode()
    {
        using var directory = TestDirectory.Create();
        var storePath = Path.Combine(directory.Path, "secure-storage.json");
        var keyPath = Path.Combine(directory.Path, "secure-storage.key");
        FileEncryptionKeyProvider.CreateKeyFile(keyPath);
        var issueOutput = await RunAsync(
            [
                "pairing",
                "issue",
                $"com.example.cli.{Guid.NewGuid():N}",
                "--code",
                "--host-address",
                "192.0.2.10",
                "--json",
                "--data-dir",
                directory.Path,
                "--secret-store-file",
                storePath,
                "--secret-key-file",
                keyPath
            ]);
        var issuePayload = JsonNode.Parse(issueOutput)!.AsObject();
        var inviteFilePath = issuePayload["inviteFilePath"]!.GetValue<string>();

        try
        {
            var pairingCode = issuePayload["code"]!["pairingCode"]!.GetValue<string>();

            Assert.StartsWith("ans2:", pairingCode, StringComparison.Ordinal);
            Assert.Contains(pairingCode, issueOutput, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(inviteFilePath);
        }
    }

    private static async Task<string> RunAsync(IReadOnlyList<string> arguments)
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        var exitCode = await CliApplication.RunParsedAsync(
            CliArguments.Parse(arguments),
            new CliOutput(true, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, standardError.ToString());
        return standardOutput.ToString();
    }
}
