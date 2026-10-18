using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class RepositoryTaskTests
{
    [Theory]
    [InlineData("compare", "ansight_compare_screenshot")]
    [InlineData("assert", "ansight_assert_screenshot")]
    public async Task PixelComparisonMethodsForwardIgnoreRegions(string method, string operation)
    {
        using var repository = CreateRepository(CreateTraceSingleCallModule());
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        File.WriteAllText(task.ModulePath, """
            export default async function run({ expect, ansight }) {
              const result = await ansight.screenshots.METHOD({
                ignoreRegions: [{ x: 0.8, y: 0, width: 0.2, height: 0.05 }]
              });
              expect(result.passed, { id: 'masked-comparison' }).toBe(true);
            }
            """.Replace("METHOD", method));
        var result = await CreateExecutor((name, args, _) =>
        {
            Assert.Equal(operation, name);
            var region = Assert.Single(args["ignoreRegions"]!.AsArray())!;
            Assert.Equal(0.8, region["x"]!.GetValue<double>());
            Assert.Equal(0.05, region["height"]!.GetValue<double>());
            return Task.FromResult(RequestResult.ToolResult(new JsonObject { ["passed"] = true }, false));
        }).ExecuteAsync(new RepositoryTaskExecutionRequest("images", task, "session", new JsonObject(), "images"), CancellationToken.None);
        Assert.Equal(RepositoryTaskRunStatus.Passed, result.Status);
    }

    [Theory]
    [InlineData("compare", "ansight_compare_screenshot")]
    [InlineData("evaluate", "ansight_evaluate_screenshot")]
    public async Task ImageInspectionMethodsPassReferenceAndEnforceNamedAssertions(string method, string operation)
    {
        using var repository = CreateRepository(CreateTraceSingleCallModule());
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        File.WriteAllText(task.ModulePath, """
            export default async function run({ expect, ansight }) {
              const result = await ansight.screenshots.METHOD({
                image: { frameId: 'actual' }, reference: { workspacePath: 'references/cubes.png' },
                criteria: [{id:'cubes', description:'Three cubes are visible.'}]
              });
              expect(result.passed, { id: 'visual-check' }).toBe(true);
            }
            """.Replace("METHOD", method));
        var result = await CreateExecutor((name, args, _) =>
        {
            Assert.Equal(operation, name);
            Assert.Equal(repository.RootPath, ScreenshotWorkspaceScope.RepositoryRoot);
            Assert.Equal("references/cubes.png", args["reference"]!["workspacePath"]!.GetValue<string>());
            return Task.FromResult(RequestResult.ToolResult(new JsonObject { ["passed"] = false, ["verdict"] = "inconclusive" }, false));
        }).ExecuteAsync(new RepositoryTaskExecutionRequest("images", task, "session", new JsonObject(), "images")
        { CaptureTrace = true }, CancellationToken.None);
        Assert.Equal(RepositoryTaskRunStatus.Failed, result.Status);
        Assert.Equal("visual-check", Assert.Single(result.Assertions).AssertionId);
        Assert.False(Assert.Single(result.ToolCalls).IsError);
        Assert.Null(ScreenshotWorkspaceScope.RepositoryRoot);
    }
}
