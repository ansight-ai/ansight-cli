using System.Text.Json;

namespace Ansight.Host.Explorer;

internal sealed record GettingStartedState(
    bool Opened = false,
    bool Skipped = false,
    bool NotificationSent = false,
    string? ReplayedSessionId = null,
    bool AutomationSaved = false,
    string? CapturePath = null);

internal sealed class GettingStartedStore(string applicationDataPath)
{
    private static readonly Lock gate = new();
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string filePath = Path.Combine(applicationDataPath, "explorer", "getting-started.json");

    public GettingStartedState Read()
    {
        lock (gate) return ReadCore();
    }

    public GettingStartedState Update(Func<GettingStartedState, GettingStartedState> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (gate)
        {
            var current = ReadCore();
            var next = change(current);
            if (next == current) return current;
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var temporaryPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(next, jsonOptions));
                File.Move(temporaryPath, filePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            return next;
        }
    }

    private GettingStartedState ReadCore()
    {
        if (!File.Exists(filePath)) return new GettingStartedState();
        try
        {
            return JsonSerializer.Deserialize<GettingStartedState>(File.ReadAllText(filePath), jsonOptions)
                ?? new GettingStartedState();
        }
        catch (JsonException)
        {
            return new GettingStartedState();
        }
    }
}
