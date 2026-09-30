namespace Ansight.Cli.Tests.Commands.Update;

public sealed class CliUpdateCommandsTests
{
    [Fact]
    public void CreateProgressRoutesMessagesAndRawInstallerOutputToStandardError()
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        var output = new CliOutput(
            json: true,
            standardOutput,
            standardError);
        var progress = UpdateCommands.CreateProgress(output);

        progress.Report(new CliUpdateProgress("Downloading the platform installer..."));
        progress.Report(new CliUpdateProgress("Archive: 75%\r", IsRawInstallerOutput: true));

        Assert.Equal(string.Empty, standardOutput.ToString());
        Assert.Equal(
            $"[update] Downloading the platform installer...{Environment.NewLine}Archive: 75%\r",
            standardError.ToString());
    }

    [Fact]
    public async Task PumpInstallerOutputAsync_ForwardsAndCapturesRawProgress()
    {
        const string installerOutput = "Preparing...\nDownloading: 25%\rDownloading: 100%\r\n";
        using var reader = new StringReader(installerOutput);
        var capturedOutput = new System.Text.StringBuilder();
        var forwardedOutput = new System.Text.StringBuilder();
        var progress = new CliProgress<CliUpdateProgress>(value => forwardedOutput.Append(value.Message));

        await CliInstallerRunner.PumpInstallerOutputAsync(
            reader,
            capturedOutput,
            progress,
            CancellationToken.None);

        Assert.Equal(installerOutput, capturedOutput.ToString());
        Assert.Equal(installerOutput, forwardedOutput.ToString());
    }

    [Fact]
    public void BuildFailureDetail_IncludesBothInstallerStreams()
    {
        var result = new CliInstallerProcessResult(
            1,
            "Preparing installation...",
            "Archive checksum failed.");

        var detail = CliInstallerRunner.BuildFailureDetail(result);

        Assert.Contains("stdout: Preparing installation...", detail, StringComparison.Ordinal);
        Assert.Contains("stderr: Archive checksum failed.", detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.23.1", 2026081801, "0.23.1", 2026081802, -1)]
    [InlineData("0.23.1", 2026081802, "0.23.1", 2026081801, 1)]
    [InlineData("0.23.1", 2026081801, "0.24.0", 2026081701, -1)]
    [InlineData("0.24.0-preview.2", 2026081801, "0.24.0", 2026081801, -1)]
    [InlineData("0.24.0", 2026081801, "0.24.0", 2026081801, 0)]
    public void CompareBuilds_UsesVersionThenDailyBuildNumber(
        string leftVersion,
        long leftBuildNumber,
        string rightVersion,
        long rightBuildNumber,
        int expectedSign)
    {
        var result = CliUpdateService.CompareBuilds(
            leftVersion,
            leftBuildNumber,
            rightVersion,
            rightBuildNumber);

        Assert.Equal(expectedSign, Math.Sign(result));
    }

    [Theory]
    [InlineData("\"product\": \"cli\",")]
    [InlineData("")]
    public async Task CheckAsync_ReportsNewerBuildForSameVersion(string productProperty)
    {
        var handler = new StubHttpMessageHandler(
            $$"""
            {
              {{productProperty}}
              "channel": "public",
              "version": "0.23.1",
              "buildNumber": 2026081802,
              "summary": "Second build today.",
              "publishedAt": "2026-08-18T03:00:00Z"
            }
            """);
        using var httpClient = new HttpClient(handler);
        var service = new CliUpdateService(
            httpClient,
            new CliReleaseIdentity(
                "0.23.1",
                2026081801,
                "0.23.1+abc123",
                "abc123"),
            new CliInstallationState(null, "/tmp/missing-install.json"),
            "osx-arm64");

        var result = await service.CheckAsync(null, null, null, CancellationToken.None);

        Assert.True(result.IsUpdateAvailable);
        Assert.True(result.IsVersionChange);
        Assert.Equal(2026081802, result.LatestBuildNumber);
        Assert.Equal(
            "https://ansightaus.blob.core.windows.net/builds/cli/osx-arm64/0.23.1/2026081802/ansight-cli-osx-arm64-0.23.1-2026081802.tar.gz",
            result.ArchiveUrl);
        Assert.Equal(new Uri(ReleaseChannel.PublicReleaseUrl), Assert.Single(handler.RequestUris));
    }

    [Fact]
    public async Task CheckAsync_RejectsLegacyDesktopFeed()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(
            """{"product":"legacy-desktop","channel":"public","version":"0.35.0","buildNumber":2026090300}"""));
        var service = new CliUpdateService(
            httpClient,
            new CliReleaseIdentity("0.35.0", 2026090300, "0.35.0+abc123", "abc123"),
            new CliInstallationState(null, "/tmp/missing-install.json"),
            "osx-arm64");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CheckAsync(null, null, null, CancellationToken.None));

        Assert.Contains("not the Ansight CLI", error.Message);
    }

    [Fact]
    public async Task CheckAsync_PrefersCliIdentityFromSharedReleaseFeed()
    {
        var handler = new StubHttpMessageHandler(
            """
            {
              "channel": "public",
              "version": "0.23.1",
              "buildNumber": 2026082401,
              "cliVersion": "0.25.1",
              "cliBuildNumber": 2026082501,
              "cliSummary": "CLI updater hotfix.",
              "cliPublishedAt": "2026-08-25T05:30:00Z",
              "summary": "LegacyDesktop release.",
              "publishedAt": "2026-08-24T03:34:35Z"
            }
            """);
        using var httpClient = new HttpClient(handler);
        var service = new CliUpdateService(
            httpClient,
            new CliReleaseIdentity(
                "0.25.0",
                2026082500,
                "0.25.0+abc123",
                "abc123"),
            new CliInstallationState(null, "/tmp/missing-install.json"),
            "osx-arm64");

        var result = await service.CheckAsync(null, null, null, CancellationToken.None);

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("0.25.1", result.LatestVersion);
        Assert.Equal(2026082501, result.LatestBuildNumber);
        Assert.Equal("CLI updater hotfix.", result.Summary);
        Assert.Equal(DateTimeOffset.Parse("2026-08-25T05:30:00Z"), result.PublishedAtUtc);
        Assert.Equal(
            "https://ansightaus.blob.core.windows.net/builds/cli/osx-arm64/0.25.1/2026082501/ansight-cli-osx-arm64-0.25.1-2026082501.tar.gz",
            result.ArchiveUrl);
    }

    [Fact]
    public async Task CheckAsync_PreservesInstalledPreviewFeed()
    {
        var handler = new StubHttpMessageHandler(
            """
            {
              "channel": "preview",
              "version": "0.24.0",
              "buildNumber": 2026082101,
              "summary": "Preview build."
            }
            """);
        using var httpClient = new HttpClient(handler);
        var receipt = new CliInstallationReceipt(
            "ansight.cli.installation/v1",
            "0.23.1",
            2026081801,
            "preview",
            "linux-x64",
            "https://updates.example.test/preview/release.json",
            "https://downloads.example.test/cli/preview",
            "https://example.test/archive.tar.gz",
            new string('a', 64),
            DateTimeOffset.UtcNow,
            "/opt/ansight",
            "/opt/bin");
        var service = new CliUpdateService(
            httpClient,
            new CliReleaseIdentity(
                "0.23.1",
                2026081801,
                "0.23.1+abc123",
                "abc123"),
            new CliInstallationState(receipt, "/opt/ansight/install.json"),
            "linux-x64");

        var result = await service.CheckAsync(null, null, null, CancellationToken.None);

        Assert.Equal("preview", result.Channel);
        Assert.Equal("https://downloads.example.test/cli/preview", result.DownloadBaseUrl);
        Assert.Contains("/cli/preview/linux-x64/0.24.0/2026082101/", result.ArchiveUrl, StringComparison.Ordinal);
        Assert.Equal(
            new Uri("https://updates.example.test/preview/release.json"),
            Assert.Single(handler.RequestUris));
    }

    [Theory]
    [InlineData(
        ReleaseChannel.PublicName,
        ReleaseChannel.PublicReleaseUrl,
        ReleaseChannel.PublicDownloadBaseUrl)]
    [InlineData(
        ReleaseChannel.PreviewName,
        ReleaseChannel.PreviewReleaseUrl,
        ReleaseChannel.PreviewDownloadBaseUrl)]
    public void Resolve_ReplacesStaleLocalDownloadBaseForCanonicalFeed(
        string channelName,
        string releaseUrl,
        string expectedDownloadBaseUrl)
    {
        var receipt = new CliInstallationReceipt(
            "ansight.cli.installation/v1",
            "0.34.0",
            2026090200,
            channelName,
            "osx-arm64",
            releaseUrl,
            "file:///tmp/ansight/products/cli/packages",
            "file:///tmp/ansight/archive.tar.gz",
            new string('a', 64),
            DateTimeOffset.UtcNow,
            "/opt/ansight",
            "/opt/bin");

        var result = ReleaseChannel.Resolve(null, receipt);

        Assert.Equal(releaseUrl, result.ReleaseUrl);
        Assert.Equal(expectedDownloadBaseUrl, result.DownloadBaseUrl);
    }

    [Fact]
    public void Resolve_PreservesCompleteCustomLocalFeed()
    {
        var receipt = new CliInstallationReceipt(
            "ansight.cli.installation/v1",
            "0.34.0",
            2026090200,
            "public",
            "osx-arm64",
            "file:///tmp/ansight/release.json",
            "file:///tmp/ansight/products/cli/packages",
            "file:///tmp/ansight/archive.tar.gz",
            new string('a', 64),
            DateTimeOffset.UtcNow,
            "/opt/ansight",
            "/opt/bin");

        var result = ReleaseChannel.Resolve(null, receipt);

        Assert.Equal("file:///tmp/ansight/release.json", result.ReleaseUrl);
        Assert.Equal("file:///tmp/ansight/products/cli/packages", result.DownloadBaseUrl);
    }

    [Fact]
    public async Task CheckAsync_RequiresBothExactVersionParts()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler());
        var service = new CliUpdateService(
            httpClient,
            new CliReleaseIdentity("0.23.1", 2026081801, "0.23.1+local", "local"),
            new CliInstallationState(null, "/tmp/missing-install.json"),
            "osx-arm64");

        var exception = await Assert.ThrowsAsync<CliUsageException>(() =>
            service.CheckAsync("public", "0.23.1", null, CancellationToken.None));

        Assert.Contains("--version and --build-number", exception.Message, StringComparison.Ordinal);
    }
}
