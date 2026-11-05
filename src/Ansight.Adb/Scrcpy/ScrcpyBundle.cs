using System.Security.Cryptography;
using System.Text.Json;

namespace Ansight.Adb;

internal sealed record ScrcpyBundle(string Version, Dictionary<string, string> Files)
{
    internal static ScrcpyBundle Validate(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath)!;
        var manifestPath = Path.Combine(directory, "bundle.json");
        var manifest = JsonSerializer.Deserialize<ScrcpyBundle>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (manifest?.Files is null)
        {
            throw new InvalidDataException("The bundled scrcpy manifest is incomplete. Reinstall the Ansight CLI.");
        }

        var hasRequiredMetadata = !string.IsNullOrWhiteSpace(manifest.Version)
            && manifest.Files.ContainsKey(Path.GetFileName(executablePath))
            && manifest.Files.ContainsKey("scrcpy-server");
        if (!hasRequiredMetadata)
        {
            throw new InvalidDataException("The bundled scrcpy manifest is incomplete. Reinstall the Ansight CLI.");
        }

        foreach (var file in manifest.Files)
        {
            var isLocalFile = !string.IsNullOrWhiteSpace(file.Key)
                && file.Key != "."
                && file.Key != ".."
                && file.Key.IndexOfAny(['/', '\\', ':']) < 0;
            if (!isLocalFile)
            {
                throw new InvalidDataException("The bundled scrcpy manifest contains an invalid file name.");
            }

            using var stream = File.OpenRead(Path.Combine(directory, file.Key));
            var actualHash = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(actualHash, file.Value, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Bundled scrcpy file '{file.Key}' failed its checksum. Reinstall the Ansight CLI.");
            }
        }

        return manifest;
    }
}
