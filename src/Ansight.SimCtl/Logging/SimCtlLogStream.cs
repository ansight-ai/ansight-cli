using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Ansight.SimCtl.Logging;

namespace Ansight.SimCtl;

public sealed class SimCtlLogStream : IAsyncDisposable
{
    private const int EntryBufferCapacity = 4_096;
    private readonly ISimCtlProcess process;
    private readonly Channel<SimCtlLogEntry> entries;
    private readonly CancellationTokenSource lifetimeCancellation;
    private readonly Task pumpTask;
    private int disposed;

    internal SimCtlLogStream(ISimCtlProcess process)
    {
        this.process = process ?? throw new ArgumentNullException(nameof(process));
        entries = Channel.CreateBounded<SimCtlLogEntry>(new BoundedChannelOptions(EntryBufferCapacity)
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

    public async IAsyncEnumerable<SimCtlLogEntry> ReadEntriesAsync(
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
            await Utf8ProcessLineReader.ReadAsync(
                    process.StandardOutput,
                    HandleLineAsync,
                    lifetimeCancellation.Token)
                .ConfigureAwait(false);

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
                        ? $"SimCtl log stream exited with code {exitCode}."
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

    private async ValueTask HandleLineAsync(ReadOnlyMemory<byte> line, CancellationToken cancellationToken)
    {
        if (SimCtlLogEntryParser.TryParse(line.Span, out var entry) && entry is not null)
        {
            await entries.Writer.WriteAsync(entry, cancellationToken).ConfigureAwait(false);
        }
    }
}
