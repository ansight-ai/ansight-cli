using System.Text;
using System.Text.Json;

namespace Ansight.Cli.HostConnection;

internal sealed class ControlOutputSink
{
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly TextWriter writer;

    public ControlOutputSink(TextWriter writer)
    {
        this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public TextWriter CreateTextWriter(string stream)
    {
        if (stream is not (ControlOutputStreams.StandardOutput or ControlOutputStreams.StandardError))
        {
            throw new ArgumentOutOfRangeException(nameof(stream));
        }

        return new StreamingTextWriter(value => Write(stream, value));
    }

    private void Write(string stream, string value)
    {
        if (value.Length == 0)
        {
            return;
        }

        var message = JsonSerializer.Serialize(
            new ControlOutput(ControlProtocol.OutputSchema, stream, value),
            jsonOptions);
        lock (gate)
        {
            try
            {
                writer.WriteLine(message);
                writer.Flush();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
            }
        }
    }

    private sealed class StreamingTextWriter : TextWriter
    {
        private readonly Action<string> write;

        public StreamingTextWriter(Action<string> write)
        {
            this.write = write ?? throw new ArgumentNullException(nameof(write));
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
            => write(value.ToString());

        public override void Write(char[] buffer, int index, int count)
            => write(new string(buffer, index, count));

        public override void Write(string? value)
        {
            if (value is not null)
            {
                write(value);
            }
        }

        public override void WriteLine()
            => write(NewLine);

        public override void WriteLine(string? value)
            => write((value ?? string.Empty) + NewLine);
    }
}
