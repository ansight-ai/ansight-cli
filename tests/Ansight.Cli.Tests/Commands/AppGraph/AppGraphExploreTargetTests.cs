using Ansight.Host;

namespace Ansight.Cli.Tests.Commands.AppGraph;

public sealed class AppGraphExploreTargetTests
{
    [Fact]
    public void Resolve_SelectsMostRecentlyActiveLinkedSessionForTargetApp()
    {
        var now = DateTimeOffset.UtcNow;
        var older = CreateSession("example-1", "com.example.app", now.AddMinutes(-2));
        var newer = CreateSession("example-2", "com.example.app", now.AddMinutes(-1));
        var unrelated = CreateSession("other-1", "com.example.other", now);

        var result = AppGraphExploreTargetResolver.Resolve(
            "com.example.app",
            requestedSessionId: null,
            [older, newer, unrelated],
            [older.SessionId, newer.SessionId, unrelated.SessionId]);

        Assert.Equal("com.example.app", result.AppId);
        Assert.Equal(newer.SessionId, result.Session.SessionId);
        Assert.False(result.UsedLegacySessionTarget);
    }

    [Fact]
    public void Resolve_PreservesLegacyLiveSessionTarget()
    {
        var session = CreateSession("com-example-app-42", "com.example.app", DateTimeOffset.UtcNow);

        var result = AppGraphExploreTargetResolver.Resolve(
            session.SessionId,
            requestedSessionId: null,
            [session],
            [session.SessionId]);

        Assert.Equal("com.example.app", result.AppId);
        Assert.Equal(session.SessionId, result.Session.SessionId);
        Assert.True(result.UsedLegacySessionTarget);
    }

    [Fact]
    public void Resolve_RejectsSessionOverrideForAnotherApp()
    {
        var session = CreateSession("other-1", "com.example.other", DateTimeOffset.UtcNow);

        var exception = Assert.Throws<CliUsageException>(() => AppGraphExploreTargetResolver.Resolve(
            "com.example.app",
            session.SessionId,
            [session],
            [session.SessionId]));

        Assert.Contains("belongs to app 'com.example.other'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspaceTarget_UsesTheAppConnectedToTheCurrentFolder()
    {
        using var directory = TestDirectory.Create();
        var workspacePath = Path.Combine(directory.Path, "workspace");
        var dataPath = Path.Combine(directory.Path, "data");
        Directory.CreateDirectory(workspacePath);
        Directory.CreateDirectory(dataPath);
        File.WriteAllText(
            Path.Combine(dataPath, "known-apps.json"),
            $$"""{"apps":[{"appId":"com.example.app","name":"Example","codebasePath":"{{workspacePath.Replace("\\", "\\\\", StringComparison.Ordinal)}}"}]}""");
        var arguments = CliArguments.Parse(
        [
            "app-graph",
            "explore",
            "--workspace",
            workspacePath,
            "--data-dir",
            directory.Path
        ]);

        var prepared = await AppGraphTargetPrompt.PrepareAsync(
            arguments,
            new CliOutput(false, new StringWriter(), new StringWriter()),
            standardInput: null,
            standardInputIsInteractive: false,
            CancellationToken.None);

        Assert.Equal("com.example.app", prepared.GetOption("app-id"));
        Assert.Equal(workspacePath, prepared.GetOption("workspace"));
    }

    [Fact]
    public async Task WorkspaceTarget_PromptsWhenTheFolderHasMultipleApps()
    {
        using var directory = TestDirectory.Create();
        var workspacePath = Path.Combine(directory.Path, "workspace");
        var dataPath = Path.Combine(directory.Path, "data");
        Directory.CreateDirectory(workspacePath);
        Directory.CreateDirectory(dataPath);
        var escapedWorkspacePath = workspacePath.Replace("\\", "\\\\", StringComparison.Ordinal);
        File.WriteAllText(
            Path.Combine(dataPath, "known-apps.json"),
            $$"""{"apps":[{"appId":"com.example.one","name":"One","codebasePath":"{{escapedWorkspacePath}}"},{"appId":"com.example.two","name":"Two","codebasePath":"{{escapedWorkspacePath}}"}]}""");
        var arguments = CliArguments.Parse(
        [
            "app-graph",
            "explore",
            "--workspace",
            workspacePath,
            "--data-dir",
            directory.Path
        ]);
        using var standardError = new StringWriter();

        var prepared = await AppGraphTargetPrompt.PrepareAsync(
            arguments,
            new CliOutput(false, new StringWriter(), standardError),
            new StringReader("2\n"),
            standardInputIsInteractive: true,
            CancellationToken.None,
            static (_, _, _, _) => Task.FromResult<bool?>(true));

        Assert.Equal("com.example.two", prepared.GetOption("app-id"));
        Assert.Contains("Select the workspace app", standardError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspaceTarget_OffersToLaunchWhenTheAppHasNoLiveSession()
    {
        using var directory = TestDirectory.Create();
        var workspacePath = Path.Combine(directory.Path, "workspace");
        var dataPath = Path.Combine(directory.Path, "data");
        Directory.CreateDirectory(workspacePath);
        Directory.CreateDirectory(dataPath);
        File.WriteAllText(
            Path.Combine(dataPath, "known-apps.json"),
            $$"""{"apps":[{"appId":"com.example.app","name":"Example","codebasePath":"{{workspacePath.Replace("\\", "\\\\", StringComparison.Ordinal)}}"}]}""");
        var arguments = CliArguments.Parse(
        [
            "app-graph",
            "explore",
            "--workspace",
            workspacePath,
            "--data-dir",
            directory.Path
        ]);
        using var standardError = new StringWriter();

        var prepared = await AppGraphTargetPrompt.PrepareAsync(
            arguments,
            new CliOutput(false, new StringWriter(), standardError),
            new StringReader("\n"),
            standardInputIsInteractive: true,
            CancellationToken.None,
            static (_, _, _, _) => Task.FromResult<bool?>(false));

        Assert.Equal("com.example.app", prepared.GetOption("app-id"));
        Assert.True(prepared.HasFlag("launch"));
        Assert.Contains("Launch it now?", standardError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspaceTarget_DoesNotOfferToLaunchWhenTheAppIsAlreadyConnected()
    {
        using var directory = TestDirectory.Create();
        var workspacePath = Path.Combine(directory.Path, "workspace");
        var dataPath = Path.Combine(directory.Path, "data");
        Directory.CreateDirectory(workspacePath);
        Directory.CreateDirectory(dataPath);
        File.WriteAllText(
            Path.Combine(dataPath, "known-apps.json"),
            $$"""{"apps":[{"appId":"com.example.app","name":"Example","codebasePath":"{{workspacePath.Replace("\\", "\\\\", StringComparison.Ordinal)}}"}]}""");
        var arguments = CliArguments.Parse(
        [
            "app-graph",
            "explore",
            "--workspace",
            workspacePath,
            "--data-dir",
            directory.Path
        ]);
        using var standardError = new StringWriter();

        var prepared = await AppGraphTargetPrompt.PrepareAsync(
            arguments,
            new CliOutput(false, new StringWriter(), standardError),
            new StringReader("\n"),
            standardInputIsInteractive: true,
            CancellationToken.None,
            static (_, _, _, _) => Task.FromResult<bool?>(true));

        Assert.False(prepared.HasFlag("launch"));
        Assert.DoesNotContain("Launch it now?", standardError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveAppIdForLaunch_UsesTheAppFromALegacySessionTarget()
    {
        var session = CreateSession("com-example-app-42", "com.example.app", DateTimeOffset.UtcNow);

        var appId = AppGraphExploreTargetResolver.ResolveAppIdForLaunch(
            session.SessionId,
            [session]);

        Assert.Equal(session.AppId, appId);
    }

    private static AppSessionSnapshot CreateSession(string sessionId, string appId, DateTimeOffset lastUpdatedUtc)
        => new()
        {
            SessionId = sessionId,
            AppId = appId,
            ClientName = appId,
            RemoteAddress = "127.0.0.1",
            CreatedUtc = lastUpdatedUtc.AddMinutes(-1),
            ConfigId = null,
            Status = "Connected",
            LastUpdatedUtc = lastUpdatedUtc,
            IsHistorical = false,
            MetricChannels = [],
            Metrics = []
        };
}
