using System.Runtime.InteropServices;

namespace Ansight.Cli.Commands.Host;

internal static class NativeMethods
{
    public const int SigTerm = 15;

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    public static extern int Kill(int processId, int signal);
}
