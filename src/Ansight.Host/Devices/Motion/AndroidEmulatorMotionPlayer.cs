using System.Collections.Concurrent;
using Ansight.Adb;

namespace Ansight.Host.Devices.Motion;

/// <summary>Plays one bounded sensor sequence and restores the emulator's previous value.</summary>
internal sealed class AndroidEmulatorMotionPlayer
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DeviceLocks = new(StringComparer.Ordinal);
    private readonly AdbClient adb;

    public AndroidEmulatorMotionPlayer(AdbClient adb)
    {
        this.adb = adb;
    }

    public async Task PlayAsync(
        string deviceSerial,
        IReadOnlyList<DeviceMotionSample> samples,
        CancellationToken cancellationToken = default)
    {
        ValidateDeviceSerial(deviceSerial);

        if (samples.Count is < 1 or > 100 || samples.Any(static sample => sample.HoldMilliseconds is < 10 or > 1000)
            || samples.Sum(static sample => sample.HoldMilliseconds) > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(samples),
                "Provide 1–100 samples, each held for 10–1000 ms, with a total duration of at most 10 seconds.");
        }

        var gate = DeviceLocks.GetOrAdd(deviceSerial, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var original = await adb.GetEmulatorAccelerationAsync(deviceSerial, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                foreach (var sample in samples)
                {
                    await adb.SetEmulatorAccelerationAsync(deviceSerial, sample.Acceleration, cancellationToken)
                        .ConfigureAwait(false);
                    await Task.Delay(sample.HoldMilliseconds, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                // A task timeout still needs to leave the emulator in its previous state.
                using var restoreTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await adb.SetEmulatorAccelerationAsync(deviceSerial, original, restoreTimeout.Token)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public static void ValidateDeviceSerial(string deviceSerial)
    {
        const string prefix = "emulator-";
        if (!deviceSerial.StartsWith(prefix, StringComparison.Ordinal)
            || deviceSerial.Length == prefix.Length
            || !deviceSerial.AsSpan(prefix.Length).ToArray().All(static digit => digit is >= '0' and <= '9')
            || !int.TryParse(deviceSerial.AsSpan(prefix.Length), out var port)
            || port <= 0)
        {
            throw new PlatformNotSupportedException(
                "Accelerometer playback requires an Android Emulator serial (emulator-NNNN).");
        }
    }
}
