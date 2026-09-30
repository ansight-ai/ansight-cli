namespace Ansight.Host.Audio;

/// <summary>Serializes the microphone route across hosts run by this OS user.</summary>
internal sealed class AudioRouteLease : IDisposable
{
    private readonly FileStream stream;
    private AudioRouteLease(FileStream stream) => this.stream = stream;

    public static AudioRouteLease Acquire(string key)
    {
        var userKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))))[..16];
        var directory = Path.Combine(Path.GetTempPath(), $"ansight-audio-leases-{userKey}");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var file = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".lock");
        try
        {
            return new AudioRouteLease(new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            throw new AudioInjectionException("audio-route-busy", "Another audio injection owns this microphone route. Wait for it to finish before starting recording again.");
        }
    }

    public void Dispose() => stream.Dispose();
}
