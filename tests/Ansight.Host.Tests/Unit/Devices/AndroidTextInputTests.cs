using Ansight.Adb;
using Ansight.Host.Devices;

namespace Ansight.Host.Tests.Unit.Devices;

public sealed class AndroidTextInputTests
{
    [Fact]
    public async Task TextUsesStdinPreservesSpacesAndQuotesShellCharacters()
    {
        var commands = new List<string>();
        await AndroidTextInput.SendAsync("emulator-5554", " a'b$()%s ", (arguments, input, _) =>
        {
            Assert.Equal(["-s", "emulator-5554", "shell", "-T"], arguments);
            commands.Add(input);
            return Task.FromResult(new AdbCommandResult(0, string.Empty, string.Empty));
        }, CancellationToken.None);
        Assert.Equal(new[] { "input 'text' '%sa'\"'\"'b$()'\n", "input 'text' '%'\n", "input 'text' 's%s'\n" }, commands);
    }

    [Fact]
    public async Task FailedTextStopsWithoutEchoingBackendOutput()
    {
        var calls = 0;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => AndroidTextInput.SendAsync(
            "emulator-5554", "secret%s", (_, _, _) =>
            {
                calls++;
                return Task.FromResult(new AdbCommandResult(1, "secret", "secret"));
            }, CancellationToken.None));
        Assert.Equal(1, calls);
        Assert.DoesNotContain("secret", error.Message);
    }
}
