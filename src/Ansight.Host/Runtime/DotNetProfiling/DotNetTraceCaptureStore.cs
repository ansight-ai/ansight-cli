using System.Text.Json.Serialization;

namespace Ansight.Host.Runtime.DotNetProfiling;

internal sealed class DotNetTraceCaptureStore
{
    private const string ManifestFileName = "manifest.json";
    private readonly string capturesPath;
    private readonly JsonSerializerOptions jsonOptions = new(JsonUtil.Pretty)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public DotNetTraceCaptureStore(IApplicationPaths applicationPaths)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        capturesPath = Path.Combine(applicationPaths.ApplicationDataPath, "dotnet-traces");
        PrivateStorageDirectory.Ensure(capturesPath);
    }

    public string CreateCaptureDirectory(string captureId)
    {
        var capturePath = GetCapturePath(captureId);
        PrivateStorageDirectory.Ensure(capturePath);
        PrivateStorageDirectory.Ensure(Path.Combine(capturePath, "raw"));
        PrivateStorageDirectory.Ensure(Path.Combine(capturePath, "derived"));
        PrivateStorageDirectory.Ensure(Path.Combine(capturePath, "symbols"));
        PrivateStorageDirectory.Ensure(Path.Combine(capturePath, "diagnostics"));
        return capturePath;
    }

    public string GetCapturePath(string captureId)
    {
        if (!Guid.TryParseExact(captureId, "N", out _))
        {
            throw new ArgumentException("Capture ids must be 32-character GUID values.", nameof(captureId));
        }

        return Path.Combine(capturesPath, captureId);
    }

    public async Task SaveManifestAsync(
        DotNetTraceCaptureManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var capturePath = CreateCaptureDirectory(manifest.CaptureId);
        var manifestPath = Path.Combine(capturePath, ManifestFileName);
        var temporaryPath = manifestPath + ".tmp";
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         16 * 1024,
                         FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, jsonOptions, cancellationToken);
        }

        File.Move(temporaryPath, manifestPath, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(manifestPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public bool TryLoadManifest(string captureId, out DotNetTraceCaptureManifest? manifest)
    {
        manifest = null;
        string manifestPath;
        try
        {
            manifestPath = Path.Combine(GetCapturePath(captureId), ManifestFileName);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (!File.Exists(manifestPath))
        {
            return false;
        }

        try
        {
            manifest = DeserializeManifest(File.ReadAllText(manifestPath));
            return manifest is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public IReadOnlyList<DotNetTraceCaptureManifest> ListManifests()
    {
        var manifests = new List<DotNetTraceCaptureManifest>();
        foreach (var manifestPath in Directory.EnumerateFiles(capturesPath, ManifestFileName, SearchOption.AllDirectories))
        {
            try
            {
                var manifest = DeserializeManifest(File.ReadAllText(manifestPath));
                if (manifest is not null)
                {
                    manifests.Add(manifest);
                }
            }
            catch (JsonException)
            {
            }
            catch (IOException)
            {
            }
        }

        return manifests
            .OrderByDescending(manifest => manifest.CreatedUtc)
            .ToArray();
    }

    public async Task<DotNetTraceArtifact> DescribeArtifactAsync(
        string captureId,
        string kind,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var capturePath = GetCapturePath(captureId);
        var normalizedCapturePath = Path.GetFullPath(capturePath) + Path.DirectorySeparatorChar;
        var normalizedFilePath = Path.GetFullPath(filePath);
        if (!normalizedFilePath.StartsWith(normalizedCapturePath, PathComparison))
        {
            throw new InvalidOperationException("Trace artifacts must be stored inside their capture directory.");
        }

        await using var stream = File.OpenRead(normalizedFilePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return new DotNetTraceArtifact(
            kind,
            Path.GetRelativePath(capturePath, normalizedFilePath).Replace(Path.DirectorySeparatorChar, '/'),
            stream.Length,
            Convert.ToHexString(hash).ToLowerInvariant());
    }

    public bool TryResolveArtifactPath(
        DotNetTraceCaptureManifest manifest,
        string kind,
        out string? filePath)
    {
        filePath = null;
        var artifact = manifest.Artifacts.FirstOrDefault(candidate =>
            string.Equals(candidate.Kind, kind, StringComparison.Ordinal));
        if (artifact is null)
        {
            return false;
        }

        var capturePath = GetCapturePath(manifest.CaptureId);
        var candidatePath = Path.GetFullPath(Path.Combine(capturePath, artifact.RelativePath));
        var normalizedCapturePath = Path.GetFullPath(capturePath) + Path.DirectorySeparatorChar;
        if (!candidatePath.StartsWith(normalizedCapturePath, PathComparison)
            || !File.Exists(candidatePath))
        {
            return false;
        }

        filePath = candidatePath;
        return true;
    }

    private DotNetTraceCaptureManifest? DeserializeManifest(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<DotNetTraceCaptureManifest>(json, jsonOptions);
        }
        catch (JsonException)
        {
            return TryDeserializeVersionOneManifest(json);
        }
    }

    private DotNetTraceCaptureManifest? TryDeserializeVersionOneManifest(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!TryReadString(root, "schema", out var schema)
            || !string.Equals(schema, "ansight.dotnet-trace-capture/v1", StringComparison.Ordinal)
            || !TryReadString(root, "captureId", out var captureId)
            || !TryReadDateTimeOffset(root, "createdUtc", out var createdUtc))
        {
            return null;
        }

        var projectPath = ReadOptionalString(root, "projectPath");
        var targetPath = ReadOptionalString(root, "targetPath");
        var applicationPath = targetPath ?? projectPath ?? string.Empty;
        var launchAdapter = ReadOptionalString(root, "launchAdapter");
        return new DotNetTraceCaptureManifest
        {
            Schema = schema,
            CaptureId = captureId,
            AppId = ReadOptionalString(root, "appId") ?? string.Empty,
            ApplicationPath = applicationPath,
            Platform = ResolveLegacyPlatform(launchAdapter),
            ArtifactKind = string.IsNullOrWhiteSpace(applicationPath)
                ? null
                : Path.GetExtension(applicationPath).TrimStart('.').ToLowerInvariant(),
            LaunchAdapter = launchAdapter,
            DeviceId = ReadOptionalString(root, "deviceId"),
            CapturePreset = ReadOptionalString(root, "capturePreset") ?? "startup-explain-v1",
            RequestedDurationSeconds = ReadOptionalInt32(root, "requestedDurationSeconds"),
            State = ReadLegacyState(root),
            CreatedUtc = createdUtc,
            StartedUtc = ReadOptionalDateTimeOffset(root, "startedUtc"),
            CompletedUtc = ReadOptionalDateTimeOffset(root, "completedUtc"),
            StopReason = ReadOptionalString(root, "stopReason"),
            FailureMessage = ReadOptionalString(root, "failureMessage"),
            DotNetTraceVersion = ReadOptionalString(root, "dotNetTraceVersion"),
            DotNetDsRouterVersion = ReadOptionalString(root, "dotNetDsRouterVersion"),
            AdbVersion = ReadOptionalString(root, "adbVersion"),
            XcodeVersion = ReadOptionalString(root, "xcodeVersion"),
            Artifacts = DeserializeArray<DotNetTraceArtifact>(root, "artifacts"),
            Warnings = DeserializeArray<string>(root, "warnings")
        };
    }

    private IReadOnlyList<T> DeserializeArray<T>(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element)
            ? JsonSerializer.Deserialize<T[]>(element.GetRawText(), jsonOptions) ?? []
            : [];

    private static DotNetTraceCaptureState ReadLegacyState(JsonElement root)
        => ReadOptionalString(root, "state")?.ToLowerInvariant() switch
        {
            "queued" => DotNetTraceCaptureState.Queued,
            "restoring" or "building" or "preparingruntime" => DotNetTraceCaptureState.InspectingArtifact,
            "launching" => DotNetTraceCaptureState.PreparingDevice,
            "capturing" => DotNetTraceCaptureState.Capturing,
            "derivingartifacts" => DotNetTraceCaptureState.DerivingArtifacts,
            "completed" => DotNetTraceCaptureState.Completed,
            "failed" => DotNetTraceCaptureState.Failed,
            "cancelled" => DotNetTraceCaptureState.Cancelled,
            _ => DotNetTraceCaptureState.Failed
        };

    private static string? ResolveLegacyPlatform(string? launchAdapter)
        => launchAdapter?.ToLowerInvariant() switch
        {
            "androidemulator" or "androiddevice" => DevicePlatforms.Android,
            "iossimulator" or "iosdevice" => DevicePlatforms.Ios,
            "maccatalyst" => "maccatalyst",
            _ => null
        };

    private static bool TryReadString(JsonElement root, string propertyName, out string value)
    {
        value = ReadOptionalString(root, propertyName) ?? string.Empty;
        return value.Length > 0;
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element)
           && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static int ReadOptionalInt32(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element) && element.TryGetInt32(out var value)
            ? value
            : 0;

    private static bool TryReadDateTimeOffset(
        JsonElement root,
        string propertyName,
        out DateTimeOffset value)
    {
        value = default;
        return root.TryGetProperty(propertyName, out var element)
               && element.TryGetDateTimeOffset(out value);
    }

    private static DateTimeOffset? ReadOptionalDateTimeOffset(JsonElement root, string propertyName)
        => TryReadDateTimeOffset(root, propertyName, out var value) ? value : null;

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
