namespace Ansight.Adb;

public sealed partial class AdbClient
{
    public async Task<string> WaitForEmulatorBootAsync(
        string avdName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(avdName);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var devices = await GetDevicesAsync(token).ConfigureAwait(false);
                foreach (var device in devices.Where(device => device.IsConnected
                    && device.Serial.StartsWith("emulator-", StringComparison.Ordinal)))
                {
                    var name = await GetEmulatorAvdNameAsync(device.Serial, token).ConfigureAwait(false);
                    if (!string.Equals(name, avdName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var boot = await RunAsync(
                        ["-s", device.Serial, "shell", "getprop", "sys.boot_completed"], token).ConfigureAwait(false);
                    if (!boot.IsSuccess || boot.StandardOutput.Trim() != "1")
                        continue;

                    var packages = await RunAsync(
                        ["-s", device.Serial, "shell", "pm", "path", "android"], token).ConfigureAwait(false);
                    if (packages.IsSuccess && packages.StandardOutput.TrimStart().StartsWith("package:", StringComparison.Ordinal))
                        return device.Serial;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Android emulator '{avdName}' did not finish booting within {timeout.TotalSeconds:0} seconds.");
        }
    }
}
