using System.Text;
using Ansight.Adb;

namespace Ansight.Host.Tests.Unit.NativeLogs;

public sealed class AdbLogcatStreamTests
{
    [Fact]
    public async Task ReadEntriesAsync_WhenEntryFormatIsUnsupported_ReportsFailure()
    {
        await using var stream = new AdbLogcatStream(new CompletedAdbProcess(
            "2026-08-19 08:57:46.582280 unexpected-token 1250 1250 D WifiHAL: message\n"));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in stream.ReadEntriesAsync())
            {
            }
        });

        Assert.Contains("unsupported format", exception.Message, StringComparison.Ordinal);
        Assert.Contains("instead of silently discarding", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadEntriesAsync_IgnoresBufferMarkersAndReturnsEntries()
    {
        await using var stream = new AdbLogcatStream(new CompletedAdbProcess(
            "--------- beginning of main\n"
            + "2026-08-19 08:57:46.582280 +0000 1250 1250 D WifiHAL: message\n"));
        var entries = new List<AdbLogEntry>();

        await foreach (var entry in stream.ReadEntriesAsync())
        {
            entries.Add(entry);
        }

        Assert.Equal("message", Assert.Single(entries).Message);
    }

    private sealed class CompletedAdbProcess : IAdbProcess
    {
        public CompletedAdbProcess(string standardOutput)
        {
            StandardOutput = new MemoryStream(Encoding.UTF8.GetBytes(standardOutput));
        }

        public int ProcessId => 1234;

        public Stream StandardInput { get; } = new MemoryStream();

        public Stream StandardOutput { get; }

        public Stream StandardError { get; } = new MemoryStream();

        public Task<int> Completion { get; } = Task.FromResult(0);

        public void Terminate(bool force = false)
        {
        }

        public ValueTask DisposeAsync()
        {
            StandardInput.Dispose();
            StandardOutput.Dispose();
            StandardError.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
