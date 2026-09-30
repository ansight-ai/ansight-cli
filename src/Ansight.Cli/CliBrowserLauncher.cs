using System.ComponentModel;
using System.Diagnostics;

namespace Ansight.Cli;

internal static class CliBrowserLauncher
{
    public static string? TryOpen(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = url.AbsoluteUri,
                UseShellExecute = true
            });
            return process is null ? "The default browser could not be started." : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return exception.Message;
        }
    }
}
