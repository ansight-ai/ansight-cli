using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class AppFilePushTests
{
    [Fact]
    public void LegacyArgumentTemplate_PreservesDefaultsAndExplicitArguments()
    {
        var defaults = AppFilePush.CreateHostArgumentTemplate();
        Assert.Equal(string.Empty, defaults["localFilePath"]!.GetValue<string>());
        Assert.False(defaults["overwrite"]!.GetValue<bool>());
        Assert.True(defaults["createDirectory"]!.GetValue<bool>());

        var existingArguments = new JsonObject
        {
            ["root"] = "appData",
            ["directoryPath"] = "inbox",
            ["fileName"] = "destination.txt",
            ["overwrite"] = true,
            ["createDirectory"] = false
        };
        var arguments = AppFilePush.CreateHostArgumentTemplate(" /tmp/payload.txt ", existingArguments);
        Assert.Equal("/tmp/payload.txt", arguments["localFilePath"]!.GetValue<string>());
        Assert.Equal("appData", arguments["root"]!.GetValue<string>());
        Assert.Equal("inbox", arguments["directoryPath"]!.GetValue<string>());
        Assert.Equal("destination.txt", arguments["fileName"]!.GetValue<string>());
        Assert.True(arguments["overwrite"]!.GetValue<bool>());
        Assert.False(arguments["createDirectory"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CreateRemoteArgumentsAsync_ReadsLocalFileAndBuildsPushPayload()
    {
        using var tempDirectory = TestDirectory.Create();
        var localFilePath = Path.Combine(tempDirectory.Path, "payload.txt");
        await File.WriteAllTextAsync(localFilePath, "hello app");
        var request = AppFilePush.CreateRequest(new JsonObject
        {
            ["localFilePath"] = localFilePath,
            ["root"] = "appData",
            ["directoryPath"] = "inbox",
            ["overwrite"] = true,
            ["createDirectory"] = false
        });

        var arguments = await AppFilePush.CreateRemoteArgumentsAsync(request, CancellationToken.None);

        Assert.Equal("appData", arguments["root"]?.GetValue<string>());
        Assert.Equal("inbox", arguments["directoryPath"]?.GetValue<string>());
        Assert.Equal("payload.txt", arguments["fileName"]?.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("hello app")), arguments["contentBase64"]?.GetValue<string>());
        Assert.True(arguments["overwrite"]!.GetValue<bool>());
        Assert.False(arguments["createDirectory"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CreateRemoteArgumentsAsync_RejectsDestinationFileNamePaths()
    {
        using var tempDirectory = TestDirectory.Create();
        var localFilePath = Path.Combine(tempDirectory.Path, "payload.txt");
        await File.WriteAllTextAsync(localFilePath, "hello app");
        var request = AppFilePush.CreateRequest(new JsonObject
        {
            ["localFilePath"] = localFilePath,
            ["directoryPath"] = "inbox",
            ["fileName"] = "../payload.txt"
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AppFilePush.CreateRemoteArgumentsAsync(request, CancellationToken.None));

        Assert.Contains("fileName must be a file name", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
