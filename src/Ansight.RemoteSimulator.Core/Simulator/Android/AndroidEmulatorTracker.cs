using System.Text.RegularExpressions;
using Ansight.Adb;
using Ansight.RemoteSimulator.Core.Runtime;

namespace Ansight.RemoteSimulator.Core.Simulator.Android;

public sealed partial class AndroidEmulatorTracker : IRemoteRuntimeSource, IAsyncDisposable
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);
    private readonly IAndroidEmulatorClient client;
    private readonly TimeSpan pollInterval;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private CancellationTokenSource? pollingCancellation;
    private Task? pollingTask;

    public AndroidEmulatorTracker(
        IAndroidEmulatorClient client,
        TimeSpan? pollInterval = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.pollInterval = pollInterval ?? DefaultPollInterval;
    }

    public event EventHandler<RemoteRuntimeSnapshot>? SnapshotChanged;

    public RemoteRuntimeSnapshot Current { get; private set; } = RemoteRuntimeSnapshot.Empty;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (pollingTask is not null)
        {
            return;
        }

        pollingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await RefreshAsync(pollingCancellation.Token).ConfigureAwait(false);
        pollingTask = PollAsync(pollingCancellation.Token);
    }

    public async Task<RemoteRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RemoteRuntimeSnapshot next;
            try
            {
                var adbDevices = await client.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
                var emulatorTasks = adbDevices
                    .Where(static device => IsEmulatorSerial(device.Serial))
                    .Select(device => InspectAsync(device, cancellationToken));
                var devices = await Task.WhenAll(emulatorTasks).ConfigureAwait(false);
                next = new RemoteRuntimeSnapshot(
                    DateTimeOffset.UtcNow,
                    devices
                        .OrderByDescending(static device => device.IsBooted)
                        .ThenBy(static device => device.Name, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                next = new RemoteRuntimeSnapshot(DateTimeOffset.UtcNow, Current.Devices, ex.Message);
            }

            var previous = Current;
            Current = next;
            if (!HasMeaningfulChange(previous, next))
            {
                return next;
            }

            SnapshotChanged?.Invoke(this, next);
            return next;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    public RemoteRuntimeDevice? FindDevice(string deviceSerial)
        => Current.Devices.FirstOrDefault(device =>
            string.Equals(device.Identifier, deviceSerial, StringComparison.OrdinalIgnoreCase));

    public async Task StopEmulatorAsync(
        string deviceSerial,
        CancellationToken cancellationToken = default)
    {
        var result = await client.RunAsync(
            ["-s", deviceSerial, "emu", "kill"],
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(BuildFailureMessage("stop the emulator", result));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (pollingCancellation is not null)
        {
            await pollingCancellation.CancelAsync().ConfigureAwait(false);
        }

        if (pollingTask is not null)
        {
            try
            {
                await pollingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is an expected completion path.
            }
        }

        pollingCancellation?.Dispose();
        refreshGate.Dispose();
    }

    private async Task<RemoteRuntimeDevice> InspectAsync(
        AdbDevice device,
        CancellationToken cancellationToken)
    {
        if (!device.IsConnected)
        {
            return CreateDevice(device, null, null, null, isBooted: false);
        }

        var propertiesTask = client.RunAsync(
            ["-s", device.Serial, "shell", "getprop"],
            cancellationToken);
        var displaySizeTask = client.RunAsync(
            ["-s", device.Serial, "shell", "wm", "size"],
            cancellationToken);
        var avdNameTask = client.RunAsync(
            ["-s", device.Serial, "emu", "avd", "name"],
            cancellationToken);
        await Task.WhenAll(propertiesTask, displaySizeTask, avdNameTask).ConfigureAwait(false);

        var properties = propertiesTask.Result.IsSuccess
            ? ParseProperties(propertiesTask.Result.StandardOutput)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var avdName = FirstNonEmpty(
            GetProperty(properties, "ro.boot.qemu.avd_name"),
            ParseAvdName(avdNameTask.Result));
        var apiLevel = GetProperty(properties, "ro.build.version.sdk");
        var isBooted = string.Equals(
            GetProperty(properties, "sys.boot_completed"),
            "1",
            StringComparison.Ordinal);
        var displaySize = displaySizeTask.Result.IsSuccess
            ? ParseDisplaySize(displaySizeTask.Result.StandardOutput)
            : null;
        return CreateDevice(device, avdName, apiLevel, displaySize, isBooted);
    }

    private static RemoteRuntimeDevice CreateDevice(
        AdbDevice device,
        string? avdName,
        string? apiLevel,
        AndroidDisplaySize? displaySize,
        bool isBooted)
    {
        var name = FirstNonEmpty(avdName, device.Model, device.Device, device.Serial)
            .Replace('_', ' ');
        var runtime = string.IsNullOrWhiteSpace(apiLevel)
            ? "Android emulator"
            : $"Android API {apiLevel}";
        return new RemoteRuntimeDevice(
            device.Serial,
            name,
            isBooted ? "Booted" : device.State,
            isBooted,
            runtime,
            "android",
            displaySize?.Width,
            displaySize?.Height,
            BootIdentifier: avdName);
    }

    internal static IReadOnlyDictionary<string, string> ParseProperties(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = PropertyPattern().Match(line);
            if (match.Success)
            {
                result[match.Groups["name"].Value] = match.Groups["value"].Value;
            }
        }

        return result;
    }

    internal static AndroidDisplaySize? ParseDisplaySize(string output)
    {
        AndroidDisplaySize? result = null;
        foreach (var match in DisplaySizePattern().Matches(output).Cast<Match>())
        {
            if (int.TryParse(match.Groups["width"].Value, out var width)
                && int.TryParse(match.Groups["height"].Value, out var height)
                && width > 0
                && height > 0)
            {
                result = new AndroidDisplaySize(width, height);
            }
        }

        return result;
    }

    private static string? ParseAvdName(AdbCommandResult result)
        => !result.IsSuccess
            ? null
            : result.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(static line => !string.Equals(line, "OK", StringComparison.OrdinalIgnoreCase));

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(pollInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool HasMeaningfulChange(RemoteRuntimeSnapshot previous, RemoteRuntimeSnapshot next)
        => !string.Equals(previous.Error, next.Error, StringComparison.Ordinal)
           || !previous.Devices.SequenceEqual(next.Devices);

    private static bool IsEmulatorSerial(string serial)
        => serial.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase);

    private static string? GetProperty(IReadOnlyDictionary<string, string> properties, string name)
        => properties.TryGetValue(name, out var value) ? value : null;

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim()
           ?? string.Empty;

    private static string BuildFailureMessage(string operation, AdbCommandResult result)
    {
        var detail = FirstNonEmpty(result.StandardError, result.StandardOutput);
        return string.IsNullOrWhiteSpace(detail)
            ? $"ADB failed to {operation} with exit code {result.ExitCode}."
            : $"ADB failed to {operation}: {detail}";
    }

    [GeneratedRegex(@"^\[(?<name>[^\]]+)\]: \[(?<value>.*)\]$", RegexOptions.CultureInvariant)]
    private static partial Regex PropertyPattern();

    [GeneratedRegex(@"(?:Physical|Override) size:\s*(?<width>\d+)x(?<height>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DisplaySizePattern();
}
