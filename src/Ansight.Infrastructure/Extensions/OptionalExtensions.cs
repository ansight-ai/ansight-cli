namespace Ansight.Infrastructure.Extensions;

/// <summary>Constructs statically linked optional services only when requested.</summary>
public sealed class OptionalExtensions : IDisposable
{
    private static readonly object registrationGate = new();
    private static readonly Dictionary<string, Func<object, IOptionalExtension>> factories = new(StringComparer.Ordinal);
    private readonly object context;
    private readonly string directory;
    private readonly object gate = new();
    private readonly Dictionary<string, IOptionalExtension> modules = new(StringComparer.Ordinal);
    private bool disposed;

    public OptionalExtensions(object context, string? directory = null)
    {
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        this.directory = Path.GetFullPath(directory ?? Path.Combine(AppContext.BaseDirectory, "extensions"));
    }

    public static void Register(string module, Func<object, IOptionalExtension> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        ArgumentNullException.ThrowIfNull(factory);
        if (!System.Text.RegularExpressions.Regex.IsMatch(module, "^[A-Za-z][A-Za-z0-9.]+$"))
            throw new ArgumentException("Invalid extension name.", nameof(module));
        lock (registrationGate) factories.Add(module, factory);
    }

    // Availability checks do not construct credential stores.
    public bool IsAvailable(string module = "Ansight.Cloud.Host")
    {
        lock (registrationGate) return factories.ContainsKey(module);
    }

    public bool IsLoaded(string module = "Ansight.Cloud.Host")
    {
        lock (gate) return modules.ContainsKey(module);
    }

    public T GetService<T>(string module = "Ansight.Cloud.Host") where T : class
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!modules.TryGetValue(module, out var extension))
            {
                Func<object, IOptionalExtension>? factory;
                lock (registrationGate) factories.TryGetValue(module, out factory);
                if (factory is null) throw new OptionalExtensionUnavailableException(module);
                extension = factory(context);
                modules.Add(module, extension);
            }
            return extension.GetService(typeof(T)) as T
                ?? throw new InvalidOperationException($"Extension '{module}' does not implement {typeof(T).FullName}.");
        }
    }

    public T? TryGetLoadedService<T>(string module = "Ansight.Cloud.Host") where T : class
    {
        lock (gate) return modules.TryGetValue(module, out var extension) && extension is ILoadedExtensionServices loaded
            ? loaded.TryGetLoadedService(typeof(T)) as T : null;
    }

    public System.Text.Json.Nodes.JsonArray ReadOperationDefinitions(string module = "Ansight.Cloud.Host")
    {
        var path = Path.Combine(directory, module, "operations.json");
        if (!IsAvailable(module) || !File.Exists(path)) return [];
        using var stream = File.OpenRead(path);
        return System.Text.Json.Nodes.JsonNode.Parse(stream)?["operations"]?.AsArray() ?? [];
    }

    public bool HasOperation(string name) => ReadOperationDefinitions().Any(definition => definition?["name"]?.GetValue<string>() == name);

    public IReadOnlyDictionary<string, string> ReadUiEntryPoints(string module = "Ansight.Cloud.Host")
    {
        var path = Path.Combine(directory, module, "ui.json");
        if (!IsAvailable(module) || !File.Exists(path)) return new Dictionary<string, string>();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
    }

    public string? ResolveUiAsset(string route)
    {
        var segments = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4 || segments[0] != "api" || segments[1] != "extensions") return null;
        var module = segments[2];
        if (!IsAvailable(module)) return null;
        var assetRoot = Path.GetFullPath(Path.Combine(directory, module, "ui")) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(assetRoot, Path.Combine(segments[3..])));
        return path.StartsWith(assetRoot, StringComparison.Ordinal) && File.Exists(path) ? path : null;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var module in modules.Values) module.Dispose();
            modules.Clear();
        }
    }
}

public sealed class OptionalExtensionUnavailableException(string module)
    : InvalidOperationException($"This feature requires the optional {module} component. Local developer features work without it.");
