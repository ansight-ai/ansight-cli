using System.Text.Json;

namespace Ansight.Host.Audio.Ios;

/// <summary>Defers a confirmed stale route's restart until the user launches a fresh app run.</summary>
internal sealed class IosAudioRouteRecovery(string applicationDataPath)
{
    private readonly string directory = Path.Combine(applicationDataPath, "ios-audio-route-recovery");

    internal void Record(AudioTarget target)
    {
        var path = MarkerPath(target.DeviceId);
        Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new PendingRestart(target.AppId, DateTimeOffset.UtcNow)));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal async Task<bool> RecoverBeforeLaunchAsync(
        string deviceId,
        string appId,
        Func<CancellationToken, Task<DateTimeOffset?>> lastBoot,
        Func<CancellationToken, Task> restart,
        CancellationToken cancellationToken)
    {
        var path = MarkerPath(deviceId);
        if (!File.Exists(path)) return false;

        cancellationToken.ThrowIfCancellationRequested();
        using var lease = AudioRouteLease.Acquire("ios:simulator-audio-route");
        if (!File.Exists(path)) return false;

        PendingRestart pending;
        try
        {
            pending = JsonSerializer.Deserialize<PendingRestart>(File.ReadAllText(path))
                ?? throw new InvalidDataException("The pending Simulator audio recovery record is invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The pending Simulator audio recovery record is invalid; restart the simulator manually.", exception);
        }
        if (!string.Equals(pending.AppId, appId, StringComparison.Ordinal)) return false;

        var bootedAt = await lastBoot(cancellationToken).ConfigureAwait(false);
        var restartRequired = !bootedAt.HasValue || bootedAt.Value <= pending.DetectedUtc;
        if (restartRequired)
        {
            await restart(cancellationToken).ConfigureAwait(false);
        }

        // A failed/cancelled restart keeps the marker for the next explicit launch.
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(path);
        return restartRequired;
    }

    private string MarkerPath(string deviceId)
        => Path.Combine(directory, Guid.Parse(deviceId).ToString("N") + ".json");

    private sealed record PendingRestart(string AppId, DateTimeOffset DetectedUtc);
}
