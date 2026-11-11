using System.Text.Json.Nodes;

namespace Ansight.Cli.Tests.Commands.Companion;

public sealed class CompanionFileMutationTests
{
    [Fact]
    public void RenameStaysInFolderAndNeverOverwrites()
    {
        var arguments = Arguments();
        arguments["name"] = "renamed.json";
        var mutation = CliRemoteFileMutation.Create("files.rename", arguments);

        Assert.Equal("files.move_file", mutation.ToolId);
        Assert.Equal("config/renamed.json", mutation.Arguments["destinationPath"]!.GetValue<string>());
        Assert.False(mutation.Arguments["overwrite"]!.GetValue<bool>());
        Assert.Equal("version-1", mutation.VerificationArguments["expectedVersion"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("/outside.json")]
    [InlineData("folder/name.json")]
    [InlineData("..")]
    [InlineData("current.json")]
    public void RenameRejectsPathsAndUnchangedNames(string name)
    {
        var arguments = Arguments();
        arguments["name"] = name;

        Assert.Throws<ArgumentException>(() => CliRemoteFileMutation.Create("files.rename", arguments));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/outside")]
    [InlineData("config/../outside")]
    public void MutationCannotTargetOutsideSandbox(string path)
    {
        var arguments = Arguments();
        arguments["path"] = path;
        arguments["confirmed"] = true;

        Assert.Throws<ArgumentException>(() => CliRemoteFileMutation.Create("files.delete", arguments));
    }

    [Fact]
    public void DeleteRequiresConfirmationAndVersion()
    {
        var arguments = Arguments();
        Assert.Throws<ArgumentException>(() => CliRemoteFileMutation.Create("files.delete", arguments));
        arguments["confirmed"] = true;
        Assert.Equal("files.delete_file", CliRemoteFileMutation.Create("files.delete", arguments).ToolId);
        arguments.Remove("expectedVersion");
        Assert.Throws<ArgumentException>(() => CliRemoteFileMutation.Create("files.delete", arguments));
    }

    [Fact]
    public void EditAcceptsEmptyTextButRejectsBinaryAndOversizedContent()
    {
        var arguments = Arguments();
        arguments["contentBase64"] = "";
        var mutation = CliRemoteFileMutation.Create("files.write", arguments);
        Assert.Equal("files.push_file", mutation.ToolId);
        Assert.False(mutation.Arguments["createDirectory"]!.GetValue<bool>());

        arguments["path"] = "root.json";
        Assert.Equal(".", CliRemoteFileMutation.Create("files.write", arguments).Arguments["directoryPath"]!.GetValue<string>());

        foreach (var bytes in new[] { new byte[] { 0 }, new byte[] { 255 }, new byte[32769] })
        {
            arguments["contentBase64"] = Convert.ToBase64String(bytes);
            Assert.Throws<ArgumentException>(() => CliRemoteFileMutation.Create("files.write", arguments));
        }
    }

    private static JsonObject Arguments()
    {
        return new JsonObject
        {
            ["root"] = "documents",
            ["path"] = "config/current.json",
            ["expectedVersion"] = "version-1"
        };
    }
}
