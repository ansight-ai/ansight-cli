using Ansight.Host.Workspaces.Catalog;

namespace Ansight.Host.Tests.Unit.Workspaces;

public sealed class WorkspacePromptReferenceTests
{
    [Theory]
    [InlineData("yaml", "appId: demo\nprompt: Tap Copy and verify the confirmation appears.")]
    [InlineData("json", "{\"appId\":\"demo\",\"prompt\":\"Tap Copy and verify the confirmation appears.\"}")]
    public void PromptOnlyDefinitionsKeepChecksWithoutValidation(string extension, string source)
    {
        var test = WorkspaceTestCatalog.Parse("ansight/tests", $"ansight/tests/copy.{extension}", source);
        Assert.Empty(test.Validation.Assertions);
        Assert.Empty(test.Validation.Prompt);
        var runner = test.BuildRunnerPrompt();
        Assert.Contains("Tap Copy and verify the confirmation appears.", runner);
        Assert.Contains("Capture a requested transient check when it occurs.", runner);
        Assert.Contains("These runner instructions add no new product requirements.", runner);
        Assert.DoesNotContain("Validation:", runner);
    }

    [Theory]
    [InlineData("validation: {}")]
    [InlineData("validation: ''")]
    [InlineData("validation: []")]
    [InlineData("validation:\n  assertions: {}")]
    public void ExplicitMalformedValidationIsStillRejected(string validation)
        => Assert.Throws<InvalidDataException>(() => WorkspaceTestCatalog.Parse("ansight/tests", "ansight/tests/copy.yaml",
            "appId: demo\nprompt: Verify Copy.\n" + validation));

    [Fact]
    public void ExpandsReferencesInPlaceAndPreservesOrderAndPunctuation()
    {
        var test = WorkspaceTestCatalog.Parse("ansight/tests", "ansight/tests/copy.yaml", """
            appId: demo
            prompt: |-
              Run @task/map.open with location "Eagle Rock".
              Tap @selector/com.example%3Aid%2Fcopy.
              Verify the confirmation. Then tap @selector/Profile%20tab.
            validation:
              assertions:
                - Verify @selector/profile-title is visible after @task/map.open.
            """);
        Assert.Equal(["map.open"], test.GetReferencedTaskIds());
        var runner = test.BuildRunnerPrompt();
        Assert.Contains("Run repository task \"map.open\" with location \"Eagle Rock\".", runner);
        Assert.Contains("{\"automationId\":\"com.example:id/copy\",\"matchMode\":\"exact\"}.", runner);
        Assert.Contains("Verify the confirmation. Then tap UI element matching selector", runner);
        Assert.Contains("\"automationId\":\"Profile tab\"", runner);
        Assert.DoesNotContain("@task/", runner);
        Assert.DoesNotContain("@selector/", runner);
    }

    [Fact]
    public void DoesNotInterpretEscapesEmailAddressesOrYamlCommentsAsReferences()
    {
        var text = @"Literal \@task/abc and someone@task/abc; use `@selector/copy`.";
        Assert.Equal("copy", Assert.Single(WorkspacePromptReferences.Read(text)).Id);
        var test = WorkspaceTestCatalog.Parse("ansight/tests", "ansight/tests/copy.yaml", """
            # @task/missing
            appId: demo
            prompt: Check Home. # @task/missing
            """);
        Assert.Empty(test.GetReferencedTaskIds());
    }

    [Theory]
    [InlineData("Run @task/")]
    [InlineData("Tap @selector/%XY")]
    [InlineData("Tap @selector/%0A")]
    public void MalformedReferencesAreRejectedDuringParsing(string prompt)
        => Assert.Throws<InvalidDataException>(() => WorkspaceTestCatalog.Parse("ansight/tests", "ansight/tests/copy.yaml",
            "appId: demo\nprompt: " + prompt));
}
