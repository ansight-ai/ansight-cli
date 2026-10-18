using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class RepositoryTaskTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(null)]
    public void Loader_PublishesOptionalStateHintsThroughTaskDiscovery(int? schemaVersion)
    {
        var module = CreatePassingModule().Replace("\"schemaVersion\": 1,", schemaVersion.HasValue ? "\"schemaVersion\": 1," : "", StringComparison.Ordinal)
            .Replace("\"title\": \"Validate map\",", """
                "title": "Validate map",
                "startingState": "  Map tab is open with input.searchArea selected.  ",
                "successState": "Selection is preserved; current area pins match cached data.",
                """, StringComparison.Ordinal);
        using var repository = CreateRepository(module);

        var loaded = LoadRepository(repository.RootPath);

        Assert.Empty(loaded.Warnings);
        var task = Assert.Single(loaded.Tasks);
        Assert.Equal(1, task.SchemaVersion);
        Assert.Equal("Map tab is open with input.searchArea selected.", task.StartingState);
        Assert.Equal(task.StartingState, task.ToPublicDefinition().StartingState);
        Assert.Equal(task.SuccessState, task.ToPublicDefinition().SuccessState);
        var discovery = RepositoryTaskProtocol.BuildDiscoveryResult(loaded, "com.example.app", "session", null, null, 10);
        var descriptor = Assert.Single(discovery["tasks"]!.AsArray())!;
        Assert.Equal(task.StartingState, descriptor["startingState"]!.GetValue<string>());
        Assert.Equal(task.SuccessState, descriptor["successState"]!.GetValue<string>());
    }

    [Fact]
    public void Loader_ExistingTasksDoNotRequireStateHints()
    {
        using var repository = CreateRepository(CreatePassingModule());
        var loaded = LoadRepository(repository.RootPath);
        Assert.Empty(loaded.Warnings);
        var task = Assert.Single(loaded.Tasks);
        Assert.Null(task.StartingState);
        Assert.Null(task.SuccessState);
        var discovery = RepositoryTaskProtocol.BuildDiscoveryResult(loaded, "com.example.app", "session", null, null, 10);
        var descriptor = Assert.Single(discovery["tasks"]!.AsArray())!.AsObject();
        Assert.False(descriptor.ContainsKey("startingState"));
        Assert.False(descriptor.ContainsKey("successState"));
    }

    [Theory]
    [InlineData("startingState", "blank")]
    [InlineData("successState", "blank")]
    [InlineData("startingState", "long")]
    [InlineData("successState", "long")]
    [InlineData("startingState", "object")]
    [InlineData("successState", "object")]
    public void Loader_RejectsInvalidStateHints(string field, string kind)
    {
        var value = kind == "object" ? "{}" : JsonValue.Create(kind == "blank" ? " " : new string('x', 1001))!.ToJsonString();
        using var repository = CreateRepository(CreatePassingModule().Replace("\"title\": \"Validate map\",",
            $"\"title\": \"Validate map\", \"{field}\": {value},", StringComparison.Ordinal));

        var loaded = LoadRepository(repository.RootPath);

        Assert.Empty(loaded.Tasks);
        Assert.Contains(field, Assert.Single(loaded.Warnings));
    }
}
