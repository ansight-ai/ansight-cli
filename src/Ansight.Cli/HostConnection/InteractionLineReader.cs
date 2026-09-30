using System.Text;

namespace Ansight.Cli.HostConnection;

/// <summary>Bound memory before accepting a complete line, including unterminated input.</summary>
internal sealed class InteractionLineReader(TextReader reader)
{
    public Task<string?> ReadAsync(CancellationToken cancellationToken)
        // Console.In can synchronously fill a requested character buffer, continuing
        // past newlines until it is full or reaches EOF. Read one character at a time
        // so the newline, not a 4 KB buffer, frames each command. Readers still retain
        // their own efficient stream buffers. One worker per line keeps synchronous,
        // cancellation-insensitive console reads off the independent response pump.
        => Task.Run(async () =>
        {
            var line = new StringBuilder();
            var character = new char[1];
            while (await reader.ReadAsync(character.AsMemory(), cancellationToken).ConfigureAwait(false) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (character[0] == '\n') return line.ToString().TrimEnd('\r');
                if (line.Length == InteractionProtocol.MaximumLineCharacters)
                    throw new InvalidDataException("Interactive requests must not exceed 65536 characters per line.");
                line.Append(character[0]);
            }
            return line.Length == 0 ? null : line.ToString().TrimEnd('\r');
        }, cancellationToken).WaitAsync(cancellationToken);
}
