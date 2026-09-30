using System.Text.Json;

namespace Ansight.Cli.Tests.Commands.Licenses;

public sealed class LicensesCommandsTests
{
    [Fact]
    public void Catalog_EnrichesRuntimePackageFromLocalNuGetMetadata()
    {
        using var directory = TestDirectory.Create();
        var manifestPath = Path.Combine(directory.Path, "ansight.deps.json");
        File.WriteAllText(
            manifestPath,
            """
            {
              "libraries": {
                "Example.Package/1.2.3": { "type": "package" },
                "Ansight.Core/1.0.0": { "type": "package" },
                "Ansight.Host/1.0.0": { "type": "project" }
              }
            }
            """);
        var packageDirectory = Path.Combine(
            directory.Path,
            "packages",
            "example.package",
            "1.2.3");
        Directory.CreateDirectory(packageDirectory);
        File.WriteAllText(
            Path.Combine(packageDirectory, "Example.Package.nuspec"),
            """
            <?xml version="1.0"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>Example.Package</id>
                <version>1.2.3</version>
                <license type="expression">Apache-2.0</license>
                <projectUrl>https://example.test/package</projectUrl>
              </metadata>
            </package>
            """);

        var catalog = ThirdPartySoftwareCatalog.Load(
            manifestPath,
            Path.Combine(directory.Path, "packages"));

        var package = Assert.Single(catalog.Software, entry => entry.Name == "Example.Package");
        Assert.Equal("1.2.3", package.Version);
        Assert.Equal("Apache-2.0", package.License);
        Assert.Equal("bundled", package.Distribution);
        Assert.Equal("https://example.test/package", package.ProjectUrl);
        Assert.DoesNotContain(catalog.Software, entry => entry.Name == "Ansight.Core");
    }

    [Fact]
    public void Markdown_DistinguishesBundledAndExternalSoftware()
    {
        var catalog = ThirdPartySoftwareCatalog.Load(
            dependencyManifestPath: null,
            nuGetPackageRoot: null);

        var markdown = LicensesCommands.RenderMarkdown(catalog);

        Assert.Contains("# Ansight software attributions", markdown, StringComparison.Ordinal);
        Assert.Contains("| Tesseract OCR |", markdown, StringComparison.Ordinal);
        Assert.Contains("external-not-redistributed", markdown, StringComparison.Ordinal);
        Assert.Contains("| libdatachannel | 0.24.3 | [MPL-2.0]", markdown, StringComparison.Ordinal);
        Assert.Contains("| bundled |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void MaintainedCatalog_CoversEveryPublishedNuGetDependency()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "ansight.deps.json");
        Assert.True(File.Exists(manifestPath), $"Expected dependency manifest at {manifestPath}.");
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var catalog = ThirdPartySoftwareCatalog.Load(
            dependencyManifestPath: null,
            nuGetPackageRoot: null);

        foreach (var library in document.RootElement.GetProperty("libraries").EnumerateObject())
        {
            if (!library.Value.TryGetProperty("type", out var type)
                || type.GetString() != "package")
            {
                continue;
            }

            var separatorIndex = library.Name.LastIndexOf('/');
            Assert.True(separatorIndex > 0, $"Unexpected dependency key '{library.Name}'.");
            var name = library.Name[..separatorIndex];
            var version = library.Name[(separatorIndex + 1)..];
            if (name.Equals("Ansight", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Ansight.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Assert.Contains(
                catalog.Software,
                entry => entry.Source == "nuget"
                         && entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                         && entry.Version == version);
        }
    }

    [Fact]
    public async Task Export_WritesMarkdownInventory()
    {
        using var directory = TestDirectory.Create();
        var outputPath = Path.Combine(directory.Path, "THIRD-PARTY.md");
        var arguments = CliArguments.Parse(["licenses", "export", outputPath]);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await CliApplication.RunParsedAsync(
            arguments,
            new CliOutput(false, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.True(File.Exists(outputPath));
        Assert.Contains("# Ansight software attributions", File.ReadAllText(outputPath), StringComparison.Ordinal);
        Assert.Contains("Exported", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, standardError.ToString());
    }
}
