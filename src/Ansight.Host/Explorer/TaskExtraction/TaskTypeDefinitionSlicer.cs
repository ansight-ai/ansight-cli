using System.Text;
using System.Text.RegularExpressions;

namespace Ansight.Host.Explorer.TaskExtraction;

internal static class TaskTypeDefinitionSlicer
{
    private static readonly Regex declarationPattern = new(
        @"(?m)^export (?:interface|type|const) (?<name>[A-Za-z][A-Za-z0-9_]*)",
        RegexOptions.Compiled);
    private static readonly Regex wordPattern = new(@"\b[A-Za-z][A-Za-z0-9_]*\b", RegexOptions.Compiled);
    private static readonly Regex memberUsePattern = new(
        @"\b(?<owner>ansight|app)\.(?<feature>[A-Za-z][A-Za-z0-9_]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly IReadOnlySet<string> coreHostFeatures = new HashSet<string>(
        ["ui", "keyboard", "session"], StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlySet<string> coreAppFeatures = new HashSet<string>(
        ["ui", "maui", "react", "flutter", "capacitor"], StringComparer.OrdinalIgnoreCase);

    internal static string Slice(string compactDeclarations, string description, string seedSource)
    {
        ArgumentNullException.ThrowIfNull(compactDeclarations);
        var evidence = description + "\n" + seedSource;
        var requestedHostFeatures = new HashSet<string>(coreHostFeatures, StringComparer.OrdinalIgnoreCase);
        var requestedAppFeatures = new HashSet<string>(coreAppFeatures, StringComparer.OrdinalIgnoreCase);
        foreach (Match match in memberUsePattern.Matches(evidence))
        {
            var feature = match.Groups["feature"].Value;
            if (match.Groups["owner"].Value.Equals("ansight", StringComparison.OrdinalIgnoreCase))
                requestedHostFeatures.Add(feature);
            else
                requestedAppFeatures.Add(feature);
        }

        AddWhenMentioned(evidence, @"\b(copy|copied|paste|clipboard|share)\b", requestedAppFeatures, "clipboard");
        AddWhenMentioned(evidence, @"\b(artifact|attachment)\b|@artifact", requestedHostFeatures, "artifacts");
        AddWhenMentioned(evidence, @"\b(artifact|attachment)\b|@artifact", requestedAppFeatures, "artifacts");
        AddWhenMentioned(evidence, @"\b(database|sql|query data)\b", requestedHostFeatures, "database");
        AddWhenMentioned(evidence, @"\b(database|sql|query data)\b", requestedAppFeatures, "data");
        AddWhenMentioned(evidence, @"\b(network|http|request|response)\b", requestedHostFeatures, "network");
        AddWhenMentioned(evidence, @"\b(telemetry|metric)\b", requestedHostFeatures, "telemetry");
        AddWhenMentioned(evidence, @"\b(logs?|logging)\b", requestedHostFeatures, "logs");
        AddWhenMentioned(evidence, @"\b(screenshot|image comparison)\b", requestedHostFeatures, "screenshots");
        AddWhenMentioned(evidence, @"\b(permission|authorization|grant access)\b", requestedHostFeatures, "permissions");
        AddWhenMentioned(evidence, @"\b(file|folder|directory|export|import)\b", requestedAppFeatures, "files");
        AddWhenMentioned(evidence, @"\b(preference|setting)\b", requestedAppFeatures, "preferences");
        AddWhenMentioned(evidence, @"\b(secure storage|keychain|keystore)\b", requestedAppFeatures, "secureStorage");

        var withoutUnusedMembers = FilterContextMembers(
            FilterContextMembers(compactDeclarations, "AnsightHost", requestedHostFeatures),
            "TaskAppContext", requestedAppFeatures);
        var matches = declarationPattern.Matches(withoutUnusedMembers).Cast<Match>().ToArray();
        var declarations = matches.Select((match, index) => new Declaration(
            match.Groups["name"].Value,
            withoutUnusedMembers[match.Index..(index + 1 < matches.Length ? matches[index + 1].Index : withoutUnusedMembers.Length)]))
            .ToArray();
        var declarationsByName = declarations.ToLookup(declaration => declaration.Name, StringComparer.Ordinal);
        var included = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(["TaskDefinition", "TaskFunction", "TaskInvocation"]);
        if (requestedHostFeatures.Contains("permissions"))
        {
            pending.Enqueue("Permission");
            pending.Enqueue("IosPermission");
            pending.Enqueue("AndroidPermission");
        }
        while (pending.TryDequeue(out var name))
        {
            if (!included.Add(name)) continue;
            foreach (var declaration in declarationsByName[name])
            {
                foreach (Match word in wordPattern.Matches(declaration.Source))
                {
                    var dependency = word.Value;
                    if (!included.Contains(dependency) && declarationsByName.Contains(dependency)) pending.Enqueue(dependency);
                }
            }
        }

        var result = new StringBuilder(withoutUnusedMembers.Length);
        foreach (var declaration in declarations)
        {
            if (included.Contains(declaration.Name)) result.Append(declaration.Source);
        }
        return result.ToString();
    }

    private static string FilterContextMembers(
        string declarations,
        string contextName,
        IReadOnlySet<string> requestedFeatures)
    {
        var contextPattern = new Regex($@"(?ms)^export interface {Regex.Escape(contextName)}\s*\{{.*?^\}}");
        var contextMatch = contextPattern.Match(declarations);
        if (!contextMatch.Success) return declarations;
        var filteredContext = Regex.Replace(
            contextMatch.Value,
            @"(?m)^  readonly (?<feature>[A-Za-z][A-Za-z0-9_]*): Task[A-Za-z0-9_]+Context;\n?",
            match => requestedFeatures.Contains(match.Groups["feature"].Value) ? match.Value : string.Empty);
        return declarations[..contextMatch.Index] + filteredContext + declarations[(contextMatch.Index + contextMatch.Length)..];
    }

    private static void AddWhenMentioned(string evidence, string pattern, ISet<string> features, string feature)
    {
        if (Regex.IsMatch(evidence, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            features.Add(feature);
    }

    private sealed record Declaration(string Name, string Source);
}
