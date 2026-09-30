namespace Ansight.Analytics;

/// <summary>
/// Non-secret account attribution shared by CLI processes and the resident host.
/// This is telemetry metadata, never an authentication or authorization source.
/// </summary>
public static class AnalyticsAccount
{
    private sealed record Context(string Directory, string? AccountId, string? BusinessId);
    private static readonly AsyncLocal<Context?> current = new();

    public static string? Read(string dataDirectory)
    {
        try
        {
            var root = Path.GetFullPath(dataDirectory);
            if (current.Value is { } context && context.Directory == root) return context.AccountId;
            return ReadPersisted(root);
        }
        catch { return null; }
    }

    public static string? ReadPersisted(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(Path.GetFullPath(dataDirectory), "analytics", "account-id");
            return File.Exists(path) && new FileInfo(path).Length <= 64
                && Guid.TryParse(File.ReadAllText(path), out var account) ? account.ToString("D") : null;
        }
        catch { return null; }
    }

    public static string? Business(string dataDirectory)
        => current.Value is { } context && context.Directory == Path.GetFullPath(dataDirectory) ? context.BusinessId : null;

    public static void Update(string dataDirectory, string? accountId)
    {
        string? temporary = null;
        try
        {
            var path = Path.Combine(Path.GetFullPath(dataDirectory), "analytics", "account-id");
            var value = Guid.TryParse(accountId, out var account) ? account.ToString("D") : "anonymous";
            if (File.Exists(path) && new FileInfo(path).Length <= 64 && File.ReadAllText(path) == value) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new StreamWriter(new FileStream(temporary, options))) stream.Write(value);
            File.Move(temporary, path, true);
            ProductUsage.Flush(dataDirectory, force: true);
            new EventOutbox(Path.Combine(Path.GetFullPath(dataDirectory), "analytics")).RemoveDetailedEvents();
        }
        catch { /* Attribution must never affect authentication. */ }
        finally { try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    // Captures even an anonymous actor, so an operation cannot inherit a later login.
    public static IDisposable Capture(string dataDirectory) => Capture(dataDirectory, Read(dataDirectory), Business(dataDirectory));
    public static IDisposable Capture(string dataDirectory, string? accountId, string? businessId = null)
    {
        var previous = current.Value;
        current.Value = new(Path.GetFullPath(dataDirectory), Guid.TryParse(accountId, out var id) ? id.ToString("D") : null, Guid.TryParse(businessId, out var business) ? business.ToString("D") : null);
        return new Scope(previous);
    }
    private sealed class Scope(Context? previous) : IDisposable
    {
        public void Dispose() => current.Value = previous;
    }
}
