using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Ansight.Host.Utilities;

internal static partial class EmbeddedTextResource
{
    private const string TemplateEndMarker = "<!-- /template -->";
    private static readonly Assembly assembly = typeof(EmbeddedTextResource).Assembly;
    private static readonly ConcurrentDictionary<string, Lazy<string>> resources = new(
        StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>>
        resourceSections = new(StringComparer.Ordinal);

    public static string Read(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        return resources.GetOrAdd(
            relativePath,
            static path => new Lazy<string>(() => ReadCore(path))).Value;
    }

    private static string ReadCore(string relativePath)
    {
        var suffix = "." + relativePath
            .Replace('/', '.')
            .Replace('\\', '.');
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(suffix, StringComparison.Ordinal))
            .ToArray();
        if (resourceNames.Length == 0)
        {
            throw new InvalidOperationException(
                $"The embedded text resource '{relativePath}' was not found.");
        }

        if (resourceNames.Length > 1)
        {
            throw new InvalidOperationException(
                $"The embedded text resource path '{relativePath}' is ambiguous.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceNames[0])
                           ?? throw new InvalidOperationException(
                               $"The embedded text resource '{relativePath}' could not be opened.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string Render(
        string relativePath,
        IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return RenderCore(relativePath, Read(relativePath), values);
    }

    public static string ReadSection(string relativePath, string sectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);
        return resourceSections
            .GetOrAdd(
                relativePath,
                static _ => new ConcurrentDictionary<string, string>(StringComparer.Ordinal))
            .GetOrAdd(
                sectionName,
                name => ReadSectionCore(relativePath, name));
    }

    public static string RenderSection(
        string relativePath,
        string sectionName,
        IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return RenderCore(
            $"{relativePath}#{sectionName}",
            ReadSection(relativePath, sectionName),
            values);
    }

    private static string ReadSectionCore(string relativePath, string sectionName)
    {
        var content = Read(relativePath);
        var startMarker = $"<!-- template:{sectionName} -->";
        var startIndex = content.IndexOf(startMarker, StringComparison.Ordinal);
        if (startIndex < 0)
        {
            throw new InvalidOperationException(
                $"The embedded text resource '{relativePath}' does not contain template section '{sectionName}'.");
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
                $"Template section '{sectionName}' in embedded text resource '{relativePath}' has no closing marker.");
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
                    $"The embedded text resource '{resourceDescription}' contains an unknown placeholder '{name}'.");
        });

    [GeneratedRegex("\\$([A-Z][A-Z0-9_]*)\\$", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();
}
