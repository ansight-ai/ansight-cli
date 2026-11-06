using System.Diagnostics;

namespace Ansight.Host.Runtime.DeviceExecution;

internal sealed record DeviceCommandResult(int ExitCode, byte[] Output, string Error)
{
    public string Text => Encoding.UTF8.GetString(Output).Trim();

    public string RequireText()
        => ExitCode == 0 ? Text : throw new IOException(string.IsNullOrWhiteSpace(Error) ? Text : Error);
}
