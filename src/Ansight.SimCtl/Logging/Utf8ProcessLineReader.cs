using System.Buffers;

namespace Ansight.SimCtl.Logging;

internal static class Utf8ProcessLineReader
{
    private const int ReadBufferSize = 64 * 1024;
    private const int MaximumLineLength = 1024 * 1024;

    public static async Task ReadAsync(
        Stream stream,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> lineHandler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(lineHandler);

        var readBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        var lineBuffer = new ArrayBufferWriter<byte>();
        var isDiscardingOversizedLine = false;
        try
        {
            while (true)
            {
                var bytesRead = await stream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                var consumed = 0;
                for (var index = 0; index < bytesRead; index++)
                {
                    if (readBuffer[index] != (byte)'\n')
                    {
                        continue;
                    }

                    if (!isDiscardingOversizedLine)
                    {
                        await HandleLineAsync(
                                readBuffer.AsMemory(consumed, index - consumed),
                                lineBuffer,
                                lineHandler,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    lineBuffer.Clear();
                    isDiscardingOversizedLine = false;
                    consumed = index + 1;
                }

                if (consumed >= bytesRead || isDiscardingOversizedLine)
                {
                    continue;
                }

                var remaining = readBuffer.AsSpan(consumed, bytesRead - consumed);
                if (lineBuffer.WrittenCount + remaining.Length > MaximumLineLength)
                {
                    lineBuffer.Clear();
                    isDiscardingOversizedLine = true;
                    continue;
                }

                lineBuffer.Write(remaining);
            }

            if (!isDiscardingOversizedLine && lineBuffer.WrittenCount > 0)
            {
                await lineHandler(TrimCarriageReturn(lineBuffer.WrittenMemory), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }

    private static async ValueTask HandleLineAsync(
        ReadOnlyMemory<byte> lineFragment,
        ArrayBufferWriter<byte> lineBuffer,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> lineHandler,
        CancellationToken cancellationToken)
    {
        if (lineBuffer.WrittenCount == 0)
        {
            await lineHandler(TrimCarriageReturn(lineFragment), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (lineBuffer.WrittenCount + lineFragment.Length > MaximumLineLength)
        {
            return;
        }

        lineBuffer.Write(lineFragment.Span);
        await lineHandler(TrimCarriageReturn(lineBuffer.WrittenMemory), cancellationToken).ConfigureAwait(false);
    }

    private static ReadOnlyMemory<byte> TrimCarriageReturn(ReadOnlyMemory<byte> line)
        => line.Length > 0 && line.Span[^1] == (byte)'\r' ? line[..^1] : line;
}
