using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Ansight.Cli.Commands.AppGraph;

internal static partial class AppGraphTextResource
{
    public const string CatalogFileName = "app-graph-prompts.md";
    private const string TemplateEndMarker = "<!-- /template -->";
    private static readonly Assembly assembly = typeof(AppGraphTextResource).Assembly;
    private static readonly ConcurrentDictionary<string, Lazy<string>> resources = new(
        StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>>
        resourceSections = new(StringComparer.Ordinal);

    private static string Read(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return resources.GetOrAdd(
            fileName,
            static name => new Lazy<string>(() => ReadCore(name))).Value;
    }

    public static string ReadSection(string fileName, string sectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);
        return resourceSections
            .GetOrAdd(
                fileName,
                static _ => new ConcurrentDictionary<string, string>(StringComparer.Ordinal))
            .GetOrAdd(
                sectionName,
                name => ReadSectionCore(fileName, name));
    }

    public static string RenderSection(
        string fileName,
        string sectionName,
        IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return RenderCore(
            $"{fileName}#{sectionName}",
            ReadSection(fileName, sectionName),
            values);
    }

    private static string ReadSectionCore(string fileName, string sectionName)
    {
        var content = Read(fileName);
        var startMarker = $"<!-- template:{sectionName} -->";
        var startIndex = content.IndexOf(startMarker, StringComparison.Ordinal);
        if (startIndex < 0)
        {
            throw new InvalidOperationException(
                $"The embedded App Graph text resource '{fileName}' does not contain template section '{sectionName}'.");
        }

        startIndex += startMarker.Length;
        if (content.AsSpan(startIndex).StartsWith("\r\n", StringComparison.Ordinal))
        {
            startIndex += 2;
        }
        else if (startIndex < content.Length && content[startIndex] == '\n')
        {
            startIndex++;
        }

        var endIndex = content.IndexOf(TemplateEndMarker, startIndex, StringComparison.Ordinal);
        if (endIndex < 0)
        {
            throw new InvalidOperationException(
                $"Template section '{sectionName}' in embedded App Graph text resource '{fileName}' has no closing marker.");
        }

        return content[startIndex..endIndex].TrimEnd('\r', '\n');
    }

    private static string RenderCore(
        string resourceDescription,
        string template,
        IReadOnlyDictionary<string, string> values)
        => PlaceholderPattern().Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            return values.TryGetValue(name, out var value)
                ? value
                : throw new InvalidOperationException(
                    $"The embedded App Graph text resource '{resourceDescription}' contains an unknown placeholder '{name}'.");
        });

    private static string ReadCore(string fileName)
    {
        var suffix = $".Commands.AppGraph.Resources.{fileName}";
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(suffix, StringComparison.Ordinal))
            .ToArray();
        if (resourceNames.Length == 0)
        {
            throw new InvalidOperationException(
                $"The embedded App Graph text resource '{fileName}' was not found.");
        }

        if (resourceNames.Length > 1)
        {
            throw new InvalidOperationException(
                $"The embedded App Graph text resource '{fileName}' is ambiguous.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceNames[0])
                           ?? throw new InvalidOperationException(
                               $"The embedded App Graph text resource '{fileName}' could not be opened.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex("\\$([A-Z][A-Z0-9_]*)\\$", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();
}
