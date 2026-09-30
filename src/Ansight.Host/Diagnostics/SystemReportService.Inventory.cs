using System.Text.Json.Nodes;

namespace Ansight.Host.Diagnostics;

public sealed partial class SystemReportService
{
    private async Task<SystemReportSection> CollectAgentsAsync(CancellationToken token)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var agents = new List<object>();
        var partial = false;
        foreach (var name in new[] { "codex", "claude", "gemini", "copilot", "cursor-agent" })
        {
            var configured = Environment.GetEnvironmentVariable($"ANSIGHT_{name.Replace('-', '_').ToUpperInvariant()}_PATH");
            var paths = DiagnosticProcess.FindExecutables(name, configured);
            var installations = new List<DiagnosticTool>();
            foreach (var path in paths)
                installations.Add(await DiagnosticProcess.InspectAsync(name, path, "Agent CLI", token));
            partial |= installations.Any(installation => installation.Status != "available");
            var selected = !string.IsNullOrWhiteSpace(configured) ? configured : paths.FirstOrDefault();
            agents.Add(new { name, type = "cli", status = installations.Count > 0 ? "installed" : "not-found", selectedExecutable = selected,
                installations, configurationDirectory = name switch
                {
                    "codex" => Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(home, ".codex"),
                    "claude" => Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(home, ".claude"),
                    _ => Path.Combine(home, "." + name)
                },
                integrationStatus = "not-checked" });
        }
        if (OperatingSystem.IsMacOS())
            foreach (var name in new[] { "Codex", "Claude", "Cursor", "ChatGPT", "Visual Studio Code" })
                foreach (var root in new[] { "/Applications", Path.Combine(home, "Applications") })
                {
                    var path = Path.Combine(root, name + ".app");
                    if (!Directory.Exists(path)) continue;
                    var plist = Path.Combine(path, "Contents", "Info.plist");
                    var version = await DiagnosticProcess.RunAsync("/usr/bin/plutil", ["-extract", "CFBundleShortVersionString", "raw", "-o", "-", plist], token);
                    partial |= version.Status != "available";
                    agents.Add(new { name, type = "desktop", status = "installed", path, version = version.Status == "available" ? version.Output : null, versionStatus = version.Status });
                    if (name == "Codex")
                    {
                        var bundled = Path.Combine(path, "Contents", "Resources", "codex");
                        if (File.Exists(bundled)) agents.Add(await DiagnosticProcess.InspectAsync("codex", bundled, "Codex desktop bundled CLI", token));
                    }
                }
        return new(partial ? "partial" : "complete", Node(agents));
    }

    private SystemReportSection CollectSkills(CancellationToken token)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<SkillRoot>
        {
            new("bundled", Path.Combine(AppContext.BaseDirectory, "skills")),
            new("shared-user", Path.Combine(home, ".agents", "skills")),
            new("codex-user", Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(home, ".codex"), "skills")),
            new("claude-user", Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(home, ".claude"), "skills"))
        };
        foreach (var project in ReadAppDefinitions().Select(app => app.CodebasePath).Concat(options.AutomationRepositoryPaths).Append(Environment.CurrentDirectory)
                     .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct())
            foreach (var directory in new[] { ".agents", ".codex", ".claude" })
                roots.Add(new("project:" + directory, Path.Combine(project!, directory, "skills")));
        var skills = new JsonArray();
        var incomplete = false;
        foreach (var root in roots.DistinctBy(root => root.Path).Where(root => Directory.Exists(root.Path)))
        {
            foreach (var directory in Directory.EnumerateDirectories(root.Path, "ansight-*"))
            {
                token.ThrowIfCancellationRequested();
                try { skills.Add(SkillInventory.Inspect(directory, root.Scope, Path.Combine(AppContext.BaseDirectory, "skills", Path.GetFileName(directory)), token)); }
                catch (OperationCanceledException) { throw; }
                catch { skills.Add(Node(new { id = Path.GetFileName(directory), path = directory, scope = root.Scope, comparison = "not-checked" })); incomplete = true; }
            }
        }
        return new(incomplete ? "partial" : "complete", skills);
    }
    private sealed record SkillRoot(string Scope, string Path);
}
