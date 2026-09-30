using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Ansight.Host.Runtime.NativeProfiling;

internal sealed class NativeProfileCaptureStore
{
    private const string ManifestFileName = "manifest.json";
    private readonly string capturesPath;
    private readonly JsonSerializerOptions jsonOptions = new(JsonUtil.Pretty)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public NativeProfileCaptureStore(IApplicationPaths applicationPaths)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        capturesPath = Path.Combine(applicationPaths.ApplicationDataPath, "native-profiles");
        Directory.CreateDirectory(capturesPath);
    }

    public string CreateCaptureDirectory(string captureId)
    {
        var capturePath = GetCapturePath(captureId);
        Directory.CreateDirectory(capturePath);
        Directory.CreateDirectory(Path.Combine(capturePath, "raw"));
        Directory.CreateDirectory(Path.Combine(capturePath, "derived"));
        Directory.CreateDirectory(Path.Combine(capturePath, "symbols"));
        Directory.CreateDirectory(Path.Combine(capturePath, "diagnostics"));
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
        NativeProfileCaptureManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var manifestPath = Path.Combine(CreateCaptureDirectory(manifest.CaptureId), ManifestFileName);
        var temporaryPath = manifestPath + ".tmp";
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         16 * 1024,
                         FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, jsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporaryPath, manifestPath, overwrite: true);
    }

    public bool TryLoadManifest(string captureId, out NativeProfileCaptureManifest? manifest)
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
            manifest = JsonSerializer.Deserialize<NativeProfileCaptureManifest>(
                File.ReadAllText(manifestPath),
                jsonOptions);
            return manifest is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public IReadOnlyList<NativeProfileCaptureManifest> ListManifests()
    {
        var manifests = new List<NativeProfileCaptureManifest>();
        foreach (var manifestPath in Directory.EnumerateFiles(
                     capturesPath,
                     ManifestFileName,
                     SearchOption.AllDirectories))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<NativeProfileCaptureManifest>(
                    File.ReadAllText(manifestPath),
                    jsonOptions);
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

        return manifests.OrderByDescending(static manifest => manifest.CreatedUtc).ToArray();
    }

    public async Task<NativeProfileArtifact> DescribeArtifactAsync(
        string captureId,
        NativeProfileProducedArtifact producedArtifact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(producedArtifact);
        var capturePath = GetCapturePath(captureId);
        var normalizedCapturePath = Path.GetFullPath(capturePath) + Path.DirectorySeparatorChar;
        var normalizedArtifactPath = Path.GetFullPath(producedArtifact.Path);
        if (!normalizedArtifactPath.StartsWith(normalizedCapturePath, PathComparison))
        {
            throw new InvalidOperationException("Profile artifacts must be stored inside their capture directory.");
        }

        var description = producedArtifact.IsDirectory
            ? await DescribeDirectoryAsync(normalizedArtifactPath, cancellationToken).ConfigureAwait(false)
            : await DescribeFileAsync(normalizedArtifactPath, cancellationToken).ConfigureAwait(false);
        return new NativeProfileArtifact(
            producedArtifact.Kind,
            Path.GetRelativePath(capturePath, normalizedArtifactPath)
                .Replace(Path.DirectorySeparatorChar, '/'),
            description.Length,
            description.Sha256,
            producedArtifact.IsDirectory,
            producedArtifact.Authoritative);
    }

    public bool TryResolveArtifactPath(
        NativeProfileCaptureManifest manifest,
        string kind,
        out string? artifactPath)
    {
        artifactPath = null;
        var artifact = manifest.Artifacts.FirstOrDefault(candidate => string.Equals(
            candidate.Kind,
            kind,
            StringComparison.Ordinal));
        if (artifact is null)
        {
            return false;
        }

        var capturePath = GetCapturePath(manifest.CaptureId);
        var candidatePath = Path.GetFullPath(Path.Combine(capturePath, artifact.RelativePath));
        var normalizedCapturePath = Path.GetFullPath(capturePath) + Path.DirectorySeparatorChar;
        var exists = artifact.IsDirectory ? Directory.Exists(candidatePath) : File.Exists(candidatePath);
        if (!candidatePath.StartsWith(normalizedCapturePath, PathComparison) || !exists)
        {
            return false;
        }

        artifactPath = candidatePath;
        return true;
    }

    private static async Task<ArtifactDescription> DescribeFileAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Profile artifact was not found.", filePath);
        }

        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new ArtifactDescription(stream.Length, Convert.ToHexStringLower(hash));
    }

    private static async Task<ArtifactDescription> DescribeDirectoryAsync(
        string directoryPath,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException($"Profile artifact directory '{directoryPath}' was not found.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long totalLength = 0;
        var buffer = new byte[128 * 1024];
        foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(directoryPath, filePath)
                .Replace(Path.DirectorySeparatorChar, '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
            hash.AppendData([0]);

            await using var stream = File.OpenRead(filePath);
            totalLength = checked(totalLength + stream.Length);
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0, bytesRead));
            }
        }

        return new ArtifactDescription(totalLength, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed record ArtifactDescription(long Length, string Sha256);
}
