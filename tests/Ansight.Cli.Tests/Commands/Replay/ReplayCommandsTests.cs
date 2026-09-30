using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Host.Workspaces;
using Ansight.Infrastructure.Security;

namespace Ansight.Cli.Tests.Commands.Replay;

public sealed class ReplayCommandsTests
{
    [Fact]
    public void SentryRrwebExport_BuildsSemanticClickAndCompactedInputSteps()
    {
        var root = JsonNode.Parse(
            """
            [
              [
                {
                  "type": 4,
                  "timestamp": 1700000000.0,
                  "data": { "href": "https://example.test/checkout?step=shipping" }
                },
                {
                  "type": 2,
                  "timestamp": 1700000000.1,
                  "data": {
                    "node": {
                      "type": 0,
                      "id": 1,
                      "childNodes": [
                        {
                          "type": 2,
                          "id": 2,
                          "tagName": "button",
                          "attributes": { "data-testid": "continue-checkout" },
                          "childNodes": [
                            { "type": 3, "id": 3, "textContent": "Continue", "childNodes": [] }
                          ]
                        },
                        {
                          "type": 2,
                          "id": 4,
                          "tagName": "input",
                          "attributes": { "placeholder": "Email address" },
                          "childNodes": []
                        }
                      ]
                    }
                  }
                },
                {
                  "type": 3,
                  "timestamp": 1700000001.0,
                  "data": { "source": 2, "type": 2, "id": 2, "x": 100, "y": 200 }
                },
                {
                  "type": 3,
                  "timestamp": 1700000002.0,
                  "data": { "source": 5, "id": 4, "text": "a" }
                },
                {
                  "type": 3,
                  "timestamp": 1700000002.1,
                  "data": { "source": 5, "id": 4, "text": "ada@example.test" }
                }
              ]
            ]
            """)!;

        var plan = ExternalReplayPlanBuilder.Build("sentry", "replay-1", "com.example", root);

        Assert.Equal("sentry", plan.SourceKind);
        Assert.Equal(3, plan.Steps.Count);
        Assert.Equal("navigate", plan.Steps[0].Kind);
        Assert.Contains("/checkout?step=shipping", plan.Steps[0].Instruction, StringComparison.Ordinal);
        Assert.Equal("Tap the \"Continue\" button.", plan.Steps[1].Instruction);
        Assert.Equal(
            "Enter \"ada@example.test\" into the \"Email address\" input field.",
            plan.Steps[2].Instruction);
        Assert.Empty(plan.Diagnostics);
    }

    [Fact]
    public void PostHogWrappedSnapshotData_ReportsMaskedInput()
    {
        var root = JsonNode.Parse(
            """
            {
              "event": "$snapshot_items",
              "properties": {
                "$snapshot_data": "[{\"type\":2,\"timestamp\":1700000000000,\"data\":{\"node\":{\"type\":2,\"id\":1,\"tagName\":\"input\",\"attributes\":{\"aria-label\":\"Password\"},\"childNodes\":[]}}},{\"type\":3,\"timestamp\":1700000001000,\"data\":{\"source\":5,\"id\":1,\"text\":\"••••••\"}}]"
              }
            }
            """)!;

        var plan = ExternalReplayPlanBuilder.Build("posthog", "recording-1", null, root);

        var step = Assert.Single(plan.Steps);
        Assert.Equal("input", step.Kind);
        Assert.Contains("masked the original value", step.Instruction, StringComparison.Ordinal);
        Assert.Contains(plan.Diagnostics, diagnostic =>
            diagnostic.Contains("masked", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExternalInput_PreservesWhitespaceAndRepresentsClearing()
    {
        var spacedPlan = ExternalReplayPlanBuilder.Build(
            "sentry",
            "spaced-input",
            null,
            JsonNode.Parse(
                """
                [{"type":3,"timestamp":1700000000000,"data":{"source":5,"id":1,"text":"  exact text  "}}]
                """)!);
        var clearPlan = ExternalReplayPlanBuilder.Build(
            "posthog",
            "clear-input",
            null,
            JsonNode.Parse(
                """
                [{"type":3,"timestamp":1700000000000,"data":{"source":5,"id":1,"text":""}}]
                """)!);

        Assert.Contains("\"  exact text  \"", Assert.Single(spacedPlan.Steps).Instruction, StringComparison.Ordinal);
        Assert.Equal("Clear the recorded input field.", Assert.Single(clearPlan.Steps).Instruction);
    }

    [Fact]
    public void AnsightVisualTreeValues_BuildCompactedInputStepWithSourceEvidence()
    {
        var createdUtc = DateTimeOffset.Parse("2026-08-23T01:00:00Z");
        var snapshot = new AppSessionSnapshot
        {
            SessionId = "native-input-replay",
            AppId = "com.example.native-input",
            ClientName = "Native Input",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = null,
            Status = "Complete",
            LastUpdatedUtc = createdUtc.AddSeconds(3),
            IsHistorical = true,
            Touches = [],
            VisualTreeSnapshots =
            [
                CreateInputVisualTree(createdUtc.AddSeconds(1), "tree-1", string.Empty),
                CreateInputVisualTree(createdUtc.AddSeconds(1.5), "tree-2", "a"),
                CreateInputVisualTree(createdUtc.AddSeconds(2), "tree-3", "ada@example.test")
            ],
            MetricChannels = [],
            Metrics = []
        };

        var plan = AnsightReplayPlanner.Build(snapshot);

        var step = Assert.Single(plan.Steps);
        Assert.Equal(3, plan.SourceEventCount);
        Assert.Equal("input", step.Kind);
        Assert.Contains("ada@example.test", step.Instruction, StringComparison.Ordinal);
        Assert.Equal("tree-3", step.SourceEvidence?.VisualTreeSnapshotId);
    }

    [Fact]
    public void AnsightWithoutVisualTrees_ReportsThatTextEntryCannotBeInferred()
    {
        var createdUtc = DateTimeOffset.Parse("2026-08-23T01:00:00Z");
        var snapshot = new AppSessionSnapshot
        {
            SessionId = "native-touch-only-replay",
            AppId = "com.example.native-touch-only",
            ClientName = "Native Touch Only",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = null,
            Status = "Complete",
            LastUpdatedUtc = createdUtc.AddSeconds(3),
            IsHistorical = true,
            Touches = [],
            VisualTreeSnapshots = [],
            MetricChannels = [],
            Metrics = []
        };

        var plan = AnsightReplayPlanner.Build(snapshot);

        Assert.Contains(plan.Diagnostics, diagnostic =>
            diagnostic.Contains("text entry", StringComparison.OrdinalIgnoreCase)
            && diagnostic.Contains("visual-tree", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PlanOnly_PrintsVersionedExternalReplayPlanWithoutALiveHost()
    {
        using var directory = TestDirectory.Create();
        var replayPath = Path.Combine(directory.Path, "posthog.json");
        await File.WriteAllTextAsync(
            replayPath,
            """
            [{"type":3,"timestamp":1700000000000,"data":{"source":2,"type":2,"id":42,"x":25,"y":50}}]
            """);

        var result = await RunAsync(
            ["replay", "posthog", replayPath, "--plan-only", "--app-id", "com.example", "--json"]);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        var payload = JsonNode.Parse(result.StandardOutput)!.AsObject();
        Assert.Equal("ansight.replay-run/v1", payload["schema"]!.GetValue<string>());
        Assert.True(payload["planOnly"]!.GetValue<bool>());
        Assert.StartsWith(
            "BETA FEATURE:",
            payload["betaFeatureNotice"]!.GetValue<string>(),
            StringComparison.Ordinal);
        Assert.Equal("posthog", payload["plan"]!["sourceKind"]!.GetValue<string>());
        Assert.Single(payload["plan"]!["steps"]!.AsArray());
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task UnknownOption_AbortsBeforeReplayWorkBegins()
    {
        var result = await RunAsync(
            ["replay", "ansight", "missing-source", "--plan"]);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.Contains("Unknown option '--plan'", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("--plan-only", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(ReplayCommands.BetaFeatureNotice, result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("missing-source", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingOptionValue_AbortsBeforeReplayWorkBegins()
    {
        var result = await RunAsync(
            ["replay", "ansight", "missing-source", "--model"]);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.Contains("--model requires a value", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(ReplayCommands.BetaFeatureNotice, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BooleanOptionValue_AbortsBeforeReplayWorkBegins()
    {
        var result = await RunAsync(
            ["replay", "ansight", "missing-source", "--plan-only=true"]);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.Contains("--plan-only does not accept a value", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(ReplayCommands.BetaFeatureNotice, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnsightPlanOnly_UsesRecordedTouchesAndVisualTreeTargets()
    {
        using var directory = TestDirectory.Create();
        await using var runtime = CreateRuntime(directory.Path);
        var source = await ImportAnsightReplaySessionAsync(runtime, directory.Path);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        using var context = CliCommandContext.Push(runtime, directory.Path, secretValue: null);

        var exitCode = await CliApplication.RunParsedAsync(
            CliArguments.Parse(
                ["replay", "ansight", source.SessionId, "--plan-only", "--json", "--data-dir", directory.Path]),
            new CliOutput(true, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);

        var payload = JsonNode.Parse(standardOutput.ToString())!.AsObject();
        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal("ansight", payload["plan"]!["sourceKind"]!.GetValue<string>());
        Assert.Equal(
            "Tap the visible control with automation ID \"continue-button\".",
            Assert.Single(payload["plan"]!["instructions"]!.AsArray())!.GetValue<string>());
        Assert.Equal("unreliable", payload["plan"]!["frameCadence"]!["rating"]!.GetValue<string>());
        Assert.Equal(string.Empty, standardError.ToString());
    }

    [Fact]
    public async Task AnsightPlanOnly_ReplaysFromTheSpecifiedPointAnnotation()
    {
        using var directory = TestDirectory.Create();
        await using var runtime = CreateRuntime(directory.Path);
        var source = await ImportAnsightReplaySessionAsync(runtime, directory.Path);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        using var context = CliCommandContext.Push(runtime, directory.Path, secretValue: null);

        var exitCode = await CliApplication.RunParsedAsync(
            CliArguments.Parse(
            [
                "replay", "ansight", source.SessionId,
                "--annotation", "replay-start",
                "--plan-only",
                "--json",
                "--data-dir", directory.Path
            ]),
            new CliOutput(true, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);

        var payload = JsonNode.Parse(standardOutput.ToString())!.AsObject();
        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Single(payload["plan"]!["steps"]!.AsArray());
        Assert.Contains(
            payload["plan"]!["diagnostics"]!.AsArray(),
            diagnostic => diagnostic!.GetValue<string>().Contains(
                "Replay range selected from annotation 'replay-start'",
                StringComparison.Ordinal));
        Assert.Equal(string.Empty, standardError.ToString());
    }

    [Fact]
    public void ReplayTimelineRange_UsesAnnotationBounds()
    {
        var createdUtc = DateTimeOffset.Parse("2026-08-23T01:00:00Z");
        var snapshot = CreateReplaySourceWithAnnotations(
            createdUtc,
            createdUtc.AddMinutes(1),
            [
                new SessionAnnotation
                {
                    AnnotationId = "open-guide",
                    StartUtc = createdUtc.AddSeconds(10),
                    EndUtc = createdUtc.AddSeconds(20),
                    Label = "Open guide",
                    Geometry =
                    [
                        new SessionAnnotationGeometry
                        {
                            GeometryId = "geometry-1",
                            FrameId = "frame-1",
                            CapturedAtUtc = createdUtc.AddSeconds(9),
                            Kind = SessionAnnotationGeometryKind.Rectangle,
                            X = 0.1,
                            Y = 0.2,
                            Width = 0.3,
                            Height = 0.4
                        }
                    ]
                }
            ]);

        var range = ReplayCommands.ResolveReplayTimelineRange(
            CliArguments.Parse(["replay", "ansight", snapshot.SessionId, "--annotation", "open-guide"]),
            snapshot);

        Assert.Equal(createdUtc.AddSeconds(9), range.StartUtc);
        Assert.Equal(createdUtc.AddSeconds(20), range.EndUtc);
        Assert.Equal("open-guide", range.AnnotationId);
    }

    [Fact]
    public void ReplayTimelineRange_PointAnnotationStartsReplayThroughSessionEnd()
    {
        var createdUtc = DateTimeOffset.Parse("2026-08-23T01:00:00Z");
        var snapshot = CreateReplaySourceWithAnnotations(
            createdUtc,
            createdUtc.AddMinutes(1),
            [
                new SessionAnnotation
                {
                    AnnotationId = "start-here",
                    StartUtc = createdUtc.AddSeconds(30),
                    Label = "Start here"
                }
            ]);

        var range = ReplayCommands.ResolveReplayTimelineRange(
            CliArguments.Parse(["replay", "ansight", snapshot.SessionId, "--annotation", "start-here"]),
            snapshot);

        Assert.Equal(createdUtc.AddSeconds(30), range.StartUtc);
        Assert.Equal(createdUtc.AddMinutes(1), range.EndUtc);
    }

    [Fact]
    public void ReplayTimelineRange_RejectsAnnotationCombinedWithExplicitRange()
    {
        var createdUtc = DateTimeOffset.Parse("2026-08-23T01:00:00Z");
        var snapshot = CreateReplaySourceWithAnnotations(
            createdUtc,
            createdUtc.AddMinutes(1),
            [
                new SessionAnnotation
                {
                    AnnotationId = "start-here",
                    StartUtc = createdUtc.AddSeconds(30),
                    Label = "Start here"
                }
            ]);

        var exception = Assert.Throws<CliUsageException>(() => ReplayCommands.ResolveReplayTimelineRange(
            CliArguments.Parse(
            [
                "replay", "ansight", snapshot.SessionId,
                "--annotation", "start-here",
                "--end", "45"
            ]),
            snapshot));

        Assert.Contains("cannot be combined", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnsightReplay_UnreliableFrameCadenceAbortsBeforeTargetResolution()
    {
        using var directory = TestDirectory.Create();
        await using var runtime = CreateRuntime(directory.Path);
        var source = await ImportAnsightReplaySessionAsync(runtime, directory.Path);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        using var context = CliCommandContext.Push(runtime, directory.Path, secretValue: null);

        var exitCode = await CliApplication.RunParsedAsync(
            CliArguments.Parse(
                ["replay", "ansight", source.SessionId, "--data-dir", directory.Path]),
            new CliOutput(false, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);

        Assert.True(
            exitCode == CliExitCodes.Configuration,
            $"Expected configuration exit code but received {exitCode}.{Environment.NewLine}{standardError}");
        Assert.Contains(ReplayCommands.BetaFeatureNotice, standardError.ToString(), StringComparison.Ordinal);
        Assert.Contains("Frame support is unreliable", standardError.ToString(), StringComparison.Ordinal);
        Assert.Contains("--allow-sparse-frames", standardError.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("[target.resolve]", standardError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaySessionTags_PreserveUserTagsAndReplaceThePreviousReplayStatus()
    {
        var tags = ReplayCommands.BuildReplaySessionTags(
            ["checkout", "replay-status-running", "REPLAY-DRIVEN"],
            "Sentry",
            "Succeeded");

        Assert.Equal(
            [
                "checkout",
                "replay-beta",
                "REPLAY-DRIVEN",
                "replay-source-sentry",
                "replay-status-succeeded"
            ],
            tags);
    }

    [Fact]
    public void ReplayTargetRequest_InfersTheCapturedPlatformAndNativeDevice()
    {
        var snapshot = CreateReplaySource(
            """
            {
              "device": {
                "osName": "iOS",
                "nativeDeviceId": "A1B2-C3D4"
              }
            }
            """);

        var target = ReplayCommands.ResolveReplayTargetRequest(
            CliArguments.Parse(["replay", "ansight", snapshot.SessionId]),
            snapshot);

        Assert.NotNull(target);
        Assert.Equal("ios", target.Platform);
        Assert.Equal("A1B2-C3D4", target.DeviceIdentifier);
        Assert.Null(target.ApplicationPath);
        Assert.Null(target.DeviceKind);
    }

    [Fact]
    public void ReplayTargetRequest_ExplicitTargetOptionsOverrideCapturedDeviceMetadata()
    {
        var snapshot = CreateReplaySource(
            """
            {
              "device": {
                "osName": "iOS",
                "nativeDeviceId": "captured-device"
              }
            }
            """);

        var target = ReplayCommands.ResolveReplayTargetRequest(
            CliArguments.Parse(
            [
                "replay", "ansight", snapshot.SessionId,
                "--platform", "android",
                "--device-id", "target-device",
                "--app", "/tmp/redpoint.apk",
                "--device-kind", "virtual"
            ]),
            snapshot);

        Assert.NotNull(target);
        Assert.Equal("android", target.Platform);
        Assert.Equal("target-device", target.DeviceIdentifier);
        Assert.Equal("/tmp/redpoint.apk", target.ApplicationPath);
        Assert.Equal("virtual", target.DeviceKind);
    }

    [Fact]
    public void ProviderUrisUseConfiguredScopeAndDoNotIncludeTokens()
    {
        var sentryArguments = CliArguments.Parse(
            ["replay", "sentry", "abc", "--organization", "acme org", "--project", "mobile/app"]);
        var postHogArguments = CliArguments.Parse(
            ["replay", "posthog", "abc", "--project", "42", "--host", "https://eu.posthog.com"]);

        var sentryUri = ExternalReplaySourceLoader.BuildSentryUri("abc", sentryArguments);
        var postHogUri = ExternalReplaySourceLoader.BuildPostHogUri("abc", postHogArguments);

        Assert.Equal(
            "https://sentry.io/api/0/projects/acme%20org/mobile%2Fapp/replays/abc/recording-segments/?per_page=100",
            sentryUri.AbsoluteUri);
        Assert.Equal(
            "https://eu.posthog.com/api/projects/42/session_recordings/abc/snapshots",
            postHogUri.AbsoluteUri);
    }

    [Fact]
    public async Task SentryReplayId_DownloadsRecordingSegmentsWithBearerAuthentication()
    {
        var tokenEnvironmentVariable = $"ANSIGHT_TEST_SENTRY_TOKEN_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(tokenEnvironmentVariable, "test-token");
        try
        {
            var handler = new StubHttpMessageHandler(
                """
                [[{"type":3,"timestamp":1700000000000,"data":{"source":2,"type":2,"id":42,"x":25,"y":50}}]]
                """);
            using var httpClient = new HttpClient(handler);
            using var loader = new ExternalReplaySourceLoader(httpClient);
            var arguments = CliArguments.Parse(
            [
                "replay", "sentry", "replay-42",
                "--organization", "acme",
                "--project", "mobile",
                "--token-env", tokenEnvironmentVariable
            ]);

            var plan = await loader.LoadAsync(
                "sentry",
                "replay-42",
                "com.example",
                arguments,
                CancellationToken.None);

            Assert.Equal("replay-42", plan.SourceId);
            Assert.Single(plan.Steps);
            Assert.Equal("Bearer", Assert.Single(handler.AuthorizationSchemes));
            Assert.EndsWith(
                "/api/0/projects/acme/mobile/replays/replay-42/recording-segments/?per_page=100",
                Assert.Single(handler.RequestUris).AbsoluteUri,
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(tokenEnvironmentVariable, null);
        }
    }

    private static async Task<CommandResult> RunAsync(string[] commandArguments)
    {
        var arguments = CliArguments.Parse(commandArguments);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        var exitCode = await CliApplication.RunParsedAsync(
            arguments,
            new CliOutput(arguments.IsJson, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);
        return new CommandResult(exitCode, standardOutput.ToString(), standardError.ToString());
    }

    private static RuntimeCoordinator CreateRuntime(string baseFolderPath)
    {
        var storagePath = Path.Combine(baseFolderPath, "secure-storage.json");
        var keyPath = FileEncryptionKeyProvider.ResolveDefaultKeyFilePath(storagePath);
        FileEncryptionKeyProvider.CreateKeyFile(keyPath);
        return new RuntimeCoordinator(new RuntimeOptions
        {
            BaseFolderPath = baseFolderPath,
            SecureStorageFilePath = storagePath,
            SecureStorageKeyFilePath = keyPath
        });
    }

    private static async Task<AppSessionSnapshot> ImportAnsightReplaySessionAsync(
        RuntimeCoordinator runtime,
        string directoryPath)
    {
        var createdUtc = DateTimeOffset.Parse("2026-08-23T01:00:00Z");
        var snapshot = new AppSessionSnapshot
        {
            SessionId = $"cli-agentic-replay-{Guid.NewGuid():N}",
            AppId = "com.example.agentic-replay",
            ClientName = "Agentic Replay",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = null,
            Status = "Complete",
            LastUpdatedUtc = createdUtc.AddSeconds(2),
            IsHistorical = true,
            Annotations =
            [
                new SessionAnnotation
                {
                    AnnotationId = "replay-start",
                    StartUtc = createdUtc.AddMilliseconds(500),
                    Label = "Replay start"
                }
            ],
            Touches =
            [
                CreateTouch("down", createdUtc.AddSeconds(1)),
                CreateTouch("up", createdUtc.AddSeconds(1).AddMilliseconds(100))
            ],
            VisualTreeSnapshots =
            [
                new SessionVisualTreeSnapshot
                {
                    SnapshotId = "tree-1",
                    CapturedAtUtc = createdUtc.AddSeconds(1),
                    Source = "test",
                    NodeCount = 1,
                    Payload = new JsonObject
                    {
                        ["root"] = new JsonObject
                        {
                            ["id"] = "node-1",
                            ["type"] = "Button",
                            ["automationId"] = "continue-button",
                            ["label"] = "Continue",
                            ["normalizedBounds"] = new JsonObject
                            {
                                ["x"] = 0.4,
                                ["y"] = 0.4,
                                ["width"] = 0.2,
                                ["height"] = 0.2
                            },
                            ["children"] = new JsonArray()
                        }
                    }
                }
            ],
            MetricChannels = [],
            Metrics = []
        };
        var archivePath = Path.Combine(directoryPath, "agentic-replay.zip");
        using (var stream = File.Create(archivePath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var sessionEntry = archive.CreateEntry("session.json");
            await using var sessionStream = sessionEntry.Open();
            await JsonSerializer.SerializeAsync(
                sessionStream,
                new SessionCaptureDocument
                {
                    SavedAtUtc = DateTimeOffset.UtcNow,
                    Session = snapshot
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }

        var imported = await runtime.SessionArchives.ImportSessionArchiveAsync(archivePath);
        return Assert.IsType<AppSessionSnapshot>(imported.ImportedSession);
    }

    private static SessionTouchInputRecord CreateTouch(string action, DateTimeOffset capturedAtUtc)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Action = action,
            CapturedAtUtc = capturedAtUtc,
            PointerId = 1,
            PointerIndex = 0,
            PointerCount = 1,
            X = 0.5,
            Y = 0.5,
            NormalizedX = 0.5,
            NormalizedY = 0.5,
            CoordinateUnit = "normalized"
        };

    private static SessionVisualTreeSnapshot CreateInputVisualTree(
        DateTimeOffset capturedAtUtc,
        string snapshotId,
        string value)
        => new()
        {
            SnapshotId = snapshotId,
            CapturedAtUtc = capturedAtUtc,
            Source = "test",
            NodeCount = 1,
            Payload = new JsonObject
            {
                ["root"] = new JsonObject
                {
                    ["id"] = $"{snapshotId}-input",
                    ["type"] = "Entry",
                    ["automationId"] = "email-field",
                    ["label"] = "Email",
                    ["role"] = "textbox",
                    ["supportedActions"] = new JsonArray("tap", "focus", "typeText"),
                    ["visual"] = new JsonObject { ["value"] = value },
                    ["children"] = new JsonArray()
                }
            }
        };

    private static AppSessionSnapshot CreateReplaySource(string deviceProfileJson)
        => new()
        {
            SessionId = "captured-replay",
            AppId = "com.example.replay",
            ClientName = "Replay",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = DateTimeOffset.Parse("2026-08-23T01:00:00Z"),
            ConfigId = null,
            Status = "Complete",
            LastUpdatedUtc = DateTimeOffset.Parse("2026-08-23T01:00:01Z"),
            IsHistorical = true,
            DeviceProfileJson = deviceProfileJson,
            MetricChannels = [],
            Metrics = []
        };

    private static AppSessionSnapshot CreateReplaySourceWithAnnotations(
        DateTimeOffset createdUtc,
        DateTimeOffset lastUpdatedUtc,
        IReadOnlyList<SessionAnnotation> annotations)
        => new()
        {
            SessionId = "annotated-replay",
            AppId = "com.example.replay",
            ClientName = "Replay",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = null,
            Status = "Complete",
            LastUpdatedUtc = lastUpdatedUtc,
            IsHistorical = true,
            Annotations = annotations,
            MetricChannels = [],
            Metrics = []
        };

    private sealed class RecordingTestRunGateway(WorkspaceTestRunPreparation preparation)
        : IWorkspaceTestRunGateway
    {
        public WorkspaceTestRunPreparationRequest? Request { get; private set; }

        public Task<WorkspaceTestRunPreparation> PrepareAsync(
            WorkspaceTestRunPreparationRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(preparation);
        }

        public Task<WorkspaceTestRunMeterResult> CompleteAsync(
            WorkspaceTestRunMeterCompletion completion,
            CancellationToken cancellationToken = default)
            => Task.FromResult(WorkspaceTestRunMeterResult.Success());
    }
}
