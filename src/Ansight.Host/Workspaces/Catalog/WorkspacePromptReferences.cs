using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ansight.Host.Workspaces.Catalog;

/// <summary>Inline references identify exact tasks or automation IDs; surrounding prose supplies the action.</summary>
public static partial class WorkspacePromptReferences
{
    public sealed record Reference(string Kind, string Id, int Start, int Length);

    public static IReadOnlyList<Reference> Read(string text)
    {
        var references = new List<Reference>();
        foreach (Match match in ReferencePattern().Matches(text))
        {
            var encoded = match.Groups["id"].Value.TrimEnd('.', ':');
            if (encoded.Length == 0 || Regex.IsMatch(encoded, "%($|[^0-9a-fA-F]|[0-9a-fA-F]($|[^0-9a-fA-F]))"))
                throw new InvalidDataException($"Invalid prompt reference '{match.Value}'. Supply an ID after the slash; percent-encode special characters.");
            var id = Uri.UnescapeDataString(encoded);
            if (string.IsNullOrWhiteSpace(id) || id.Any(char.IsControl))
                throw new InvalidDataException($"Invalid prompt reference '{match.Value}'. IDs must be non-empty and cannot contain control characters.");
            references.Add(new Reference(match.Groups["kind"].Value, id, match.Index,
                match.Length - match.Groups["id"].Length + encoded.Length));
        }
        return references;
    }

    public static string Expand(string text)
    {
        foreach (var reference in Read(text).Reverse())
        {
            var replacement = reference.Kind == "task"
                ? $"repository task {JsonSerializer.Serialize(reference.Id)}"
                : "UI element matching selector " + JsonSerializer.Serialize(new
                {
                    automationId = reference.Id,
                    matchMode = "exact"
                });
            text = text.Remove(reference.Start, reference.Length).Insert(reference.Start, replacement);
        }
        return text;
    }

    [GeneratedRegex(@"(?<![\w@/\\])@(?<kind>task|selector)/(?<id>[A-Za-z0-9_.:/%~\-]*)")]
    private static partial Regex ReferencePattern();
}
