using System.Diagnostics;

namespace Ansight.Host.Runtime.DeviceExecution;

internal interface IDeviceCommandRunner
{
    Task<DeviceCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken, int maximumBytes = 1_048_576);
}
