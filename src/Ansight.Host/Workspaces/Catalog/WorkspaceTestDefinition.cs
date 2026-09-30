namespace Ansight.Host.Workspaces.Catalog;

public sealed record WorkspaceTestDefinition(
    string TestId,
    string Name,
    string AppId,
    string Prompt,
    WorkspaceTestValidation Validation,
    IReadOnlyList<string> RequiredSecrets,
    string FilePath)
{
    public bool Enabled { get; init; } = true;

    public string? TaskId { get; init; }

    public string BuildRunnerPrompt(string? promptOverride = null)
    {
        var scenario = string.IsNullOrWhiteSpace(promptOverride)
            ? Prompt.Trim()
            : promptOverride.Trim();
        if (!string.IsNullOrWhiteSpace(TaskId))
        {
            scenario = $"Run repository task '{TaskId}'.\n\n{scenario}";
        }
        var validationSection = string.IsNullOrWhiteSpace(Validation.Prompt)
            ? string.Empty
            : BuildSection(
                "validation",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["VALIDATION"] = Validation.Prompt.Trim()
                });
        var assertionsSection = Validation.Assertions.Count == 0
            ? string.Empty
            : BuildSection(
                "assertions",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ASSERTIONS"] = BuildBulletList(Validation.Assertions)
                });
        var secretsSection = RequiredSecrets.Count == 0
            ? string.Empty
            : BuildSection(
                "secrets",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["SECRETS"] = BuildBulletList(RequiredSecrets)
                });
        return EmbeddedTextResource.RenderSection(
            "Workspaces/Catalog/Prompts/workspace-test-prompts.md",
            "runner",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SCENARIO"] = scenario,
                ["VALIDATION_SECTION"] = validationSection,
                ["ASSERTIONS_SECTION"] = assertionsSection,
                ["SECRETS_SECTION"] = secretsSection
            }).TrimEnd();
    }

    private static string BuildSection(
        string sectionName,
        IReadOnlyDictionary<string, string>? values = null)
    {
        var content = values is null
            ? EmbeddedTextResource.ReadSection(
                "Workspaces/Catalog/Prompts/workspace-test-prompts.md",
                sectionName)
            : EmbeddedTextResource.RenderSection(
                "Workspaces/Catalog/Prompts/workspace-test-prompts.md",
                sectionName,
                values);
        return Environment.NewLine + Environment.NewLine + content.TrimEnd();
    }

    private static string BuildBulletList(IEnumerable<string> values)
        => string.Join(
            Environment.NewLine,
            values.Select(static value => $"- {value}"));
}
