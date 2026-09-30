using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace Ansight.Cli.Commands.Setup;

internal sealed record AndroidPackage(string Name, Uri Download, long Bytes, string Sha1, string License);

internal sealed class AndroidPackageCatalog(HttpClient client)
{
    private static readonly Uri repository = new("https://dl.google.com/android/repository/");

    internal async Task<IReadOnlyList<AndroidPackage>> ResolveAsync(IReadOnlyList<string> names, CancellationToken token)
    {
        var packages = new List<AndroidPackage>();
        foreach (var group in names.GroupBy(name => name.StartsWith("system-images;", StringComparison.Ordinal)
                     ? "sys-img/google_apis/sys-img2-3.xml" : "repository2-3.xml"))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await using var stream = await client.GetStreamAsync(new Uri(repository, group.Key), timeout.Token);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = 32 * 1024 * 1024
            });
            var document = await XDocument.LoadAsync(reader, LoadOptions.None, timeout.Token);
            foreach (var name in group) packages.Add(ParsePackage(document, name, new Uri(repository, group.Key)));
        }
        return packages;
    }

    internal static AndroidPackage ParsePackage(XDocument document, string name, Uri manifest)
    {
        var package = document.Descendants().SingleOrDefault(element => element.Name.LocalName == "remotePackage" && (string?)element.Attribute("path") == name)
            ?? throw new InvalidOperationException($"Google's repository does not list Android package '{name}'.");
        var archive = package.Descendants().FirstOrDefault(element => element.Name.LocalName == "archive"
            && (Value(element, "host-os") is null or "linux"));
        var complete = archive?.Elements().FirstOrDefault(element => element.Name.LocalName == "complete")
            ?? throw new InvalidOperationException($"No Linux archive is available for '{name}'.");
        var url = Value(complete, "url") ?? throw new InvalidDataException("Android archive URL is missing.");
        var download = new Uri(manifest, url);
        if (download.Scheme != "https" || download.Host != "dl.google.com" || !download.AbsolutePath.StartsWith("/android/repository/", StringComparison.Ordinal))
            throw new InvalidDataException("Android archive must come from Google's HTTPS repository.");
        var checksum = Value(complete, "checksum") ?? "";
        var checksumElement = complete.Elements().FirstOrDefault(element => element.Name.LocalName == "checksum");
        if (checksum.Length != 40 || !checksum.All(Uri.IsHexDigit)
            || ((string?)checksumElement?.Attribute("type") is { } algorithm && algorithm != "sha1"))
            throw new InvalidDataException("Android archive has no supported repository checksum.");
        if (!long.TryParse(Value(complete, "size"), out var size) || size <= 0)
            throw new InvalidDataException("Android archive has an invalid size.");
        var licenseId = package.Elements().FirstOrDefault(element => element.Name.LocalName == "uses-license")?.Attribute("ref")?.Value;
        var license = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "license" && (string?)element.Attribute("id") == licenseId)?.Value;
        if (string.IsNullOrWhiteSpace(license)) throw new InvalidDataException($"Android package '{name}' has no license text.");
        return new(name, download, size, checksum, license);
    }

    internal async Task InstallArchiveAsync(AndroidPackage package, string sdkRoot, CancellationToken token)
    {
        var sourceName = package.Name == AndroidSetupPlan.CommandToolsPackage ? "cmdline-tools" : "platform-tools";
        var destination = package.Name == AndroidSetupPlan.CommandToolsPackage
            ? Path.Combine(sdkRoot, "cmdline-tools", "ansight-19.0") : Path.Combine(sdkRoot, sourceName);
        // Never overlay someone else's partial or customized package directory.
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException($"Android package destination already exists: {destination}. Repair it with your SDK manager or choose another --sdk-root.");
        Directory.CreateDirectory(sdkRoot);
        var staging = Path.Combine(sdkRoot, ".ansight-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var zipPath = Path.Combine(staging, "download.zip");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(20));
            await using (var source = await client.GetStreamAsync(package.Download, timeout.Token))
            await using (var destinationStream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    received += read;
                    if (received > package.Bytes) throw new InvalidDataException("Android download exceeds the repository's declared size.");
                    await destinationStream.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                }
                if (received != package.Bytes) throw new InvalidDataException("Android download was incomplete.");
            }
            await using (var file = File.OpenRead(zipPath))
            {
                var checksum = Convert.ToHexString(await SHA1.HashDataAsync(file, timeout.Token));
                if (!checksum.Equals(package.Sha1, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Android archive checksum did not match Google's repository metadata.");
            }
            var unpacked = Path.Combine(staging, "unpacked");
            ZipFile.ExtractToDirectory(zipPath, unpacked);
            var sourceDirectory = Path.Combine(unpacked, sourceName);
            var requiredExecutable = Path.Combine(sourceDirectory, sourceName == "cmdline-tools" ? "bin/sdkmanager" : "adb");
            if (!File.Exists(requiredExecutable)) throw new InvalidDataException("Android archive did not contain the expected tools.");
            if (!OperatingSystem.IsWindows())
            {
                var executables = sourceName == "cmdline-tools"
                    ? Directory.EnumerateFiles(Path.Combine(sourceDirectory, "bin"))
                    : new[] { Path.Combine(sourceDirectory, "adb"), Path.Combine(sourceDirectory, "fastboot") }.Where(File.Exists);
                foreach (var path in executables)
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(sourceDirectory, destination);
        }
        finally { Directory.Delete(staging, recursive: true); }
    }

    private static string? Value(XElement element, string name)
        => element.Elements().FirstOrDefault(child => child.Name.LocalName == name)?.Value;
}
