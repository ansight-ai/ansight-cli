using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Tests.TestSupport;
using Ansight.Infrastructure;
using Ansight.Tools;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class RepositoryModuleOperationTests
{
    [Fact]
    public async Task ListTasks_MatchesDeclaredInputDefaults()
    {
        using var environment = new TestEnvironment();
        using var repository = TestDirectory.Create();
        WriteSearchableTask(repository.Path);
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var bridge = new ContractAppToolBridge();
        var knownApps = new KnownAppStore(environment.ApplicationPaths);
        var now = DateTimeOffset.UtcNow;
        knownApps.Upsert(new KnownAppDefinition
        {
            AppId = "com.example.app",
            Name = "Example",
            IconGlyph = "E",
            CodebasePath = repository.Path,
            RepositoryAutomationsEnabled = true,
            FirstSeenUtc = now,
            LastSeenUtc = now
        });
        var catalog = CreateToolCatalog(runtimeState, environment, knownApps, bridge);

        var response = await catalog.HandleToolsCallAsync(new JsonObject
        {
            ["name"] = "ansight_list_tasks",
            ["arguments"] = new JsonObject
            {
                ["appId"] = "com.example.app",
                ["query"] = "search Sikati Bay Secret Garden 3D guide"
            }
        });

        var payload = GetStructuredContent(response);
        Assert.Equal(1, payload["matchCount"]?.GetValue<int>());
        var task = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(payload["tasks"])));
        Assert.Equal("open-area-3d-guide", task["taskId"]?.GetValue<string>());
        Assert.Contains(
            Assert.IsType<JsonArray>(task["behavioralSynonyms"]),
            node => node?.GetValue<string>() == "locate");
        var match = Assert.IsType<JsonObject>(task["match"]);
        Assert.Equal(1, match["coverage"]?.GetValue<double>());
        Assert.Empty(Assert.IsType<JsonArray>(match["unmatchedQueryTerms"]));
    }

    [Fact]
    public async Task DescribeModule_ReturnsSchemasWithoutCapabilityBoilerplate()
    {
        using var environment = new TestEnvironment();
        using var repository = TestDirectory.Create();
        WriteTask(repository.Path);
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var bridge = new ContractAppToolBridge();
        var knownApps = new KnownAppStore(environment.ApplicationPaths);
        var now = DateTimeOffset.UtcNow;
        knownApps.Upsert(new KnownAppDefinition
        {
            AppId = "com.example.app",
            Name = "Example",
            IconGlyph = "E",
            CodebasePath = repository.Path,
            RepositoryAutomationsEnabled = true,
            FirstSeenUtc = now,
            LastSeenUtc = now
        });
        var importResult = runtimeState.ImportSessionSnapshot(
            new AppSessionSnapshot
            {
                SessionId = ContractAppToolBridge.SessionId,
                AppId = "com.example.app",
                ClientName = "Example",
                RemoteAddress = "127.0.0.1",
                CreatedUtc = now,
                ConfigId = null,
                Status = "Connected",
                LastUpdatedUtc = now,
                IsHistorical = false,
                MetricChannels = [],
                Metrics = []
            },
            new Dictionary<string, byte[]>());
        Assert.True(importResult.IsSuccess);
        var catalog = CreateToolCatalog(runtimeState, environment, knownApps, bridge);

        var response = await catalog.HandleToolsCallAsync(new JsonObject
        {
            ["name"] = "ansight_describe_module",
            ["arguments"] = new JsonObject
            {
                ["sessionId"] = ContractAppToolBridge.SessionId,
                ["moduleType"] = "task",
                ["moduleId"] = "demo",
                ["includeDefinitions"] = false
            }
        });

        var payload = GetStructuredContent(response);
        Assert.Equal("demo", payload["moduleId"]?.GetValue<string>());
        Assert.Null(payload["definitionSchema"]);
        Assert.Equal("object", payload["schemas"]?["output"]?["type"]?.GetValue<string>());
        Assert.Empty(Assert.IsType<JsonArray>(payload["declaredHostTools"]));
        Assert.Null(payload["capabilities"]);
    }

    private static ToolCatalog CreateToolCatalog(
        RuntimeState runtimeState,
        TestEnvironment environment,
        KnownAppStore knownApps,
        IAppToolBridge appToolBridge)
        => new(
            runtimeState,
            environment.ApplicationPaths,
            knownApps,
            new EmptyPairingConfigService(),
            new EmptyPairingConfigCache(),
            appToolBridge,
            cloudSessionSharingService: null,
            new SessionResolver(runtimeState, appToolBridge));

    private static JsonObject GetStructuredContent(RequestResult response)
    {
        Assert.False(response.IsError);
        Assert.NotNull(response.Payload);
        Assert.False(response.Payload!["isError"]!.GetValue<bool>());
        return Assert.IsType<JsonObject>(response.Payload["structuredContent"]);
    }

    private static void WriteTask(string repositoryRootPath)
    {
        var taskDirectory = Path.Combine(repositoryRootPath, "ansight", "tasks");
        Directory.CreateDirectory(taskDirectory);
        File.WriteAllText(
            Path.Combine(taskDirectory, "demo.ts"),
            """
            export const task = {
              "schemaVersion": 1,
              "title": "Demo contract",
              "description": "Describes exact task schemas.",
              "inputSchema": {
                "type": "object",
                "properties": {},
                "additionalProperties": false
              },
              "outputSchema": {
                "type": "object",
                "properties": { "ready": { "type": "boolean" } },
                "required": ["ready"],
                "additionalProperties": false
              }
            };

            export default async function demo({ expect }) {
              expect(true, { id: "ready" }).toBeTruthy();
              return { ready: true };
            }
            """);
    }

    private static void WriteSearchableTask(string repositoryRootPath)
    {
        var taskDirectory = Path.Combine(repositoryRootPath, "ansight", "tasks");
        Directory.CreateDirectory(taskDirectory);
        File.WriteAllText(
            Path.Combine(taskDirectory, "open-area-3d-guide.ts"),
            """
            export const task = {
              "schemaVersion": 1,
              "title": "Open Secret Garden 3D guide from search",
              "description": "Searches for an area and opens its exact 3D guide.",
              "inputSchema": {
                "type": "object",
                "properties": {
                  "searchArea": { "type": "string", "default": "Sikati Bay" },
                  "targetArea": { "type": "string", "default": "Secret Garden" }
                },
                "additionalProperties": false
              },
              "outputSchema": {
                "type": "object",
                "properties": { "guideVisible": { "const": true } },
                "required": ["guideVisible"],
                "additionalProperties": false
              }
            };

            export default async function openArea3dGuide({ expect }) {
              expect(true, { id: "ready" }).toBeTruthy();
              return { guideVisible: true };
            }
            """);
    }

    private sealed class ContractAppToolBridge : IAppToolBridge
    {
        public const string SessionId = "session-contract-001";

        public event EventHandler? ConnectionsChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<string> GetConnectedSessionIds() => [SessionId];

        public bool IsSessionConnected(string sessionId)
            => string.Equals(sessionId, SessionId, StringComparison.Ordinal);

        public OperationResult ForceDisconnectSession(string sessionId)
            => OperationResult.Failure("Not supported by this test bridge.");

        public Task<AppToolBridgeResponse> QueryToolsAsync(
            string sessionId,
            CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
            => Task.FromResult(AppToolBridgeResponse.FromSuccess(
                "Catalog returned.",
                new ToolProtocolEnvelope
                {
                    Type = ToolProtocolMessageTypes.CatalogType,
                    Id = "catalog-contract-001",
                    SessionId = sessionId,
                    Payload = new JsonObject
                    {
                        ["tools"] = new JsonArray(
                            CreateTool("demo.state"),
                            CreateTool("ignored.tool"))
                    }
                }));

        public Task<AppToolBridgeResponse> CallToolAsync(
            string sessionId,
            string toolId,
            JsonObject? arguments,
            CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
            => Task.FromResult(AppToolBridgeResponse.FromFailure("Not supported by this test bridge."));

        private static JsonObject CreateTool(string toolId)
            => new()
            {
                ["id"] = toolId,
                ["name"] = toolId,
                ["argumentsSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject(),
                    ["additionalProperties"] = false
                },
                ["resultSchema"] = new JsonObject
                {
                    ["type"] = "object"
                }
            };
    }

    private sealed class EmptyPairingConfigService : IPairingConfigService
    {
        public PairingIssueResult Issue(string appName, string appId, PairingConfigDuration duration)
            => throw new NotSupportedException();
    }

    private sealed class EmptyPairingConfigCache : IPairingConfigCache
    {
        public void Add(PairingConfig config)
        {
        }

        public void Add(CachedPairingConfig config)
        {
        }

        public IReadOnlyList<CachedPairingConfig> GetSnapshot() => [];

        public CachedPairingConfig? Find(string configId) => null;

        public bool Remove(string configId) => false;
    }
}
