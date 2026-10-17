using Ansight.Adb;
using Ansight.RemoteSimulator.Core.Simulator.Android.Input;

namespace Ansight.Host.Devices;

internal static class AndroidTextInput
{
    public static async Task SendAsync(
        string deviceIdentifier,
        string text,
        Func<IReadOnlyList<string>, string, CancellationToken, Task<AdbCommandResult>> execute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var operation in TextInputEncoder.Encode(text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var verb = operation.Kind == TextInputOperationKind.Text ? "text" : "keyevent";
            var command = "input '" + verb + "' '" + operation.Value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'\n";
            // Send the shell-quoted payload over stdin, never in host process arguments.
            AdbCommandResult result;
            try
            {
                result = await execute(["-s", deviceIdentifier, "shell", "-T"], command, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (Exception)
            {
                throw new InvalidOperationException("Android text input failed.");
            }
            if (!result.IsSuccess)
                throw new InvalidOperationException($"Android text input failed with exit code {result.ExitCode}.");
        }
    }
}
