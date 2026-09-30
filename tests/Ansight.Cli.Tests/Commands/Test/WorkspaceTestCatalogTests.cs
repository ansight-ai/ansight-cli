using Ansight.Host.Workspaces;

namespace Ansight.Cli.Tests.Commands.Test;

public sealed class WorkspaceTestCatalogTests
{
    [Fact]
    public void LoadReturnsReusableValidatedTestDefinition()
    {
        using var workspace = TestDirectory.Create();
        var testsDirectory = Path.Combine(workspace.Path, "ansight", "tests", "onboarding");
        Directory.CreateDirectory(testsDirectory);
        File.WriteAllText(
            Path.Combine(testsDirectory, "sign-in.json"),
            """
            {
              "schemaVersion": 1,
              "id": "onboarding.sign-in",
              "name": "Sign in",
              "appId": "com.example.app",
              "taskId": "onboarding.sign-in",
              "prompt": "Open the sign-in page.",
              "validation": {
                "assertions": ["The sign-in form is visible."]
              },
              "requiredSecrets": ["USERNAME", "PASSWORD"]
            }
            """);

        var result = WorkspaceTestCatalog.Load(workspace.Path);

        var test = Assert.Single(result.Tests);
        Assert.Empty(result.Warnings);
        Assert.Equal("onboarding.sign-in", test.TestId);
        Assert.True(test.Enabled);
        Assert.Equal("onboarding.sign-in", test.TaskId);
        Assert.Equal(["USERNAME", "PASSWORD"], test.RequiredSecrets);
        Assert.Contains("Run repository task 'onboarding.sign-in'.", test.BuildRunnerPrompt(), StringComparison.Ordinal);
        Assert.Contains("ansight_type_secret", test.BuildRunnerPrompt(), StringComparison.Ordinal);
    }

    [Fact]
    public void LoadKeepsDisabledTestsDiscoverable()
    {
        using var workspace = TestDirectory.Create();
        var testsDirectory = Path.Combine(workspace.Path, "ansight", "tests");
        Directory.CreateDirectory(testsDirectory);
        File.WriteAllText(
            Path.Combine(testsDirectory, "disabled.json"),
            """
            {
              "schemaVersion": 1,
              "enabled": false,
              "name": "Disabled test",
              "appId": "com.example.app",
              "prompt": "Do not run this test.",
              "validation": "The test did not run."
            }
            """);

        var result = WorkspaceTestCatalog.Load(workspace.Path);

        var test = Assert.Single(result.Tests);
        Assert.Empty(result.Warnings);
        Assert.False(test.Enabled);
    }

    [Fact]
    public void LoadRejectsANonBooleanEnabledValue()
    {
        using var workspace = TestDirectory.Create();
        var testsDirectory = Path.Combine(workspace.Path, "ansight", "tests");
        Directory.CreateDirectory(testsDirectory);
        File.WriteAllText(
            Path.Combine(testsDirectory, "invalid-enabled.json"),
            """
            {
              "schemaVersion": 1,
              "enabled": "no",
              "name": "Invalid test",
              "appId": "com.example.app",
              "prompt": "Do not run this test.",
              "validation": "The test did not run."
            }
            """);

        var result = WorkspaceTestCatalog.Load(workspace.Path);

        Assert.Empty(result.Tests);
        Assert.Contains("enabled must be a boolean", Assert.Single(result.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void LoadReportsInvalidDefinitionsWithoutAbortingCatalog()
    {
        using var workspace = TestDirectory.Create();
        var testsDirectory = Path.Combine(workspace.Path, "ansight", "tests");
        Directory.CreateDirectory(testsDirectory);
        File.WriteAllText(Path.Combine(testsDirectory, "invalid.json"), "{ \"appId\": \"com.example\" }");

        var result = WorkspaceTestCatalog.Load(workspace.Path);

        Assert.Empty(result.Tests);
        Assert.Single(result.Warnings);
        Assert.Contains("prompt is required", result.Warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void LoadRejectsUnpublishedVersionTwoDefinitions()
    {
        using var workspace = TestDirectory.Create();
        var testsDirectory = Path.Combine(workspace.Path, "ansight", "tests");
        Directory.CreateDirectory(testsDirectory);
        File.WriteAllText(
            Path.Combine(testsDirectory, "version-two.json"),
            """
            {
              "schemaVersion": 2,
              "name": "Version two test",
              "appId": "com.example.app",
              "prompt": "Run the version two scenario.",
              "validation": "Verify the final state."
            }
            """);

        var result = WorkspaceTestCatalog.Load(workspace.Path);

        Assert.Empty(result.Tests);
        Assert.Contains("schemaVersion must be 1", Assert.Single(result.Warnings), StringComparison.Ordinal);
    }
}
