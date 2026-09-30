using System.Text.Json;

namespace Ansight.Infrastructure.Preferences;

public sealed class FilePreferencesStore : IPreferencesStore
{
    private readonly Lock gate = new();
    private readonly string filePath;
    private readonly JsonSerializerOptions jsonOptions;
    private Dictionary<string, JsonElement> values;

    public FilePreferencesStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(filePath));
        }

        this.filePath = filePath;
        jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };

        var parent = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(parent) && !Directory.Exists(parent))
        {
            Directory.CreateDirectory(parent);
        }

        values = LoadFromDisk(filePath, jsonOptions);
    }

    public event EventHandler<UserPreferenceChangedEventArgs>? Changed;

    public bool Contains(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        lock (gate)
        {
            return values.ContainsKey(key);
        }
    }

    public T Get<T>(string key, T defaultValue)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return defaultValue;
        }

        lock (gate)
        {
            if (!values.TryGetValue(key, out var value))
            {
                return defaultValue;
            }

            try
            {
                var typed = value.Deserialize<T>(jsonOptions);
                return typed is null ? defaultValue : typed;
            }
            catch (JsonException)
            {
                return defaultValue;
            }
        }
    }

    public void Set<T>(string key, T value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(key));
        }

        object? previousValue = null;
        var changed = false;

        lock (gate)
        {
            if (values.TryGetValue(key, out var existing))
            {
                previousValue = DeserializeObject(existing);
            }

            var next = SerializeToElement(value);

            if (values.TryGetValue(key, out var current) && JsonElement.DeepEquals(current, next))
            {
                return;
            }

            var nextValues = new Dictionary<string, JsonElement>(values, values.Comparer)
            {
                [key] = next,
            };
            PersistToDisk(filePath, nextValues, jsonOptions);
            values = nextValues;
            changed = true;
        }

        if (changed)
        {
            Changed?.Invoke(this, new UserPreferenceChangedEventArgs(key, previousValue, value));
        }
    }

    public bool Remove(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        object? previousValue = null;
        var removed = false;

        lock (gate)
        {
            if (!values.TryGetValue(key, out var existing))
            {
                return false;
            }

            previousValue = DeserializeObject(existing);
            var nextValues = new Dictionary<string, JsonElement>(values, values.Comparer);
            removed = nextValues.Remove(key);
            if (removed)
            {
                PersistToDisk(filePath, nextValues, jsonOptions);
                values = nextValues;
            }
        }

        if (removed)
        {
            Changed?.Invoke(this, new UserPreferenceChangedEventArgs(key, previousValue, currentValue: null));
        }

        return removed;
    }

    private JsonElement SerializeToElement<T>(T value)
    {
        return JsonSerializer.SerializeToElement(value, jsonOptions);
    }

    private object? DeserializeObject(JsonElement element)
    {
        try
        {
            return JsonSerializer.Deserialize<object>(element.GetRawText(), jsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, JsonElement> LoadFromDisk(string filePath, JsonSerializerOptions options)
    {
        if (!File.Exists(filePath))
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        var content = File.ReadAllText(filePath);
        if (string.IsNullOrWhiteSpace(content))
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        try
        {
            var deserialized = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(content, options);
            return deserialized ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }
    }

    private static void PersistToDisk(string filePath, Dictionary<string, JsonElement> values, JsonSerializerOptions options)
    {
        var json = JsonSerializer.Serialize(values, options);
        var parentPath = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(parentPath))
        {
            Directory.CreateDirectory(parentPath);
        }

        var tempPath = $"{filePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, filePath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
