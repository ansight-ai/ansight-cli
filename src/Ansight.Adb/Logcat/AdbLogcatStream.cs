using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Ansight.Adb;

public sealed class AdbLogcatStream : IAsyncDisposable
{
    private const int EntryBufferCapacity = 4_096;
    private readonly IAdbProcess process;
    private readonly Channel<AdbLogEntry> entries;
    private readonly CancellationTokenSource lifetimeCancellation;
    private readonly Task pumpTask;
    private int disposed;

    internal AdbLogcatStream(IAdbProcess process)
    {
        this.process = process ?? throw new ArgumentNullException(nameof(process));
        entries = Channel.CreateBounded<AdbLogEntry>(new BoundedChannelOptions(EntryBufferCapacity)
        {
            SingleReader = false,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        lifetimeCancellation = new CancellationTokenSource();
        pumpTask = PumpAsync();
    }

    public int ProcessId => process.ProcessId;

    public Task Completion => pumpTask;

    public async IAsyncEnumerable<AdbLogEntry> ReadEntriesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var entry in entries.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return entry;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lifetimeCancellation.Cancel();
        process.Terminate(force: true);
        try
        {
            await pumpTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is an expected completion path.
        }
        finally
        {
            await process.DisposeAsync().ConfigureAwait(false);
            lifetimeCancellation.Dispose();
        }
    }

    private async Task PumpAsync()
    {
        Exception? failure = null;
        try
        {
            using var standardOutputReader = new StreamReader(process.StandardOutput);
            while (!lifetimeCancellation.IsCancellationRequested)
            {
                var line = await standardOutputReader
                    .ReadLineAsync(lifetimeCancellation.Token)
                    .ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (AdbLogEntryParser.TryParse(line, out var entry) && entry is not null)
                {
                    await entries.Writer.WriteAsync(entry, lifetimeCancellation.Token).ConfigureAwait(false);
                }
                else if (!AdbLogEntryParser.IsIgnorableLine(line))
                {
                    throw new InvalidDataException(
                        "ADB logcat emitted an entry in an unsupported format. The native log stream was stopped instead of silently discarding it.");
                }
            }

            var exitCode = await process.Completion
                .WaitAsync(lifetimeCancellation.Token)
                .ConfigureAwait(false);
            if (!lifetimeCancellation.IsCancellationRequested && exitCode != 0)
            {
                using var standardErrorReader = new StreamReader(process.StandardError);
                var error = await standardErrorReader
                    .ReadToEndAsync(lifetimeCancellation.Token)
                    .ConfigureAwait(false);
                failure = new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error)
                        ? $"ADB logcat exited with code {exitCode}."
                        : error.Trim());
            }
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            entries.Writer.TryComplete(failure);
        }
    }

}
