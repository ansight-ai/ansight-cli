using Ansight.Host.Tests.TestSupport;
using Ansight.Tools;
using Ansight.Host.UiAutomation.Navigation;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class ElectronUiInputDriverTests
{
    [Fact]
    public async Task RoutesOnlyTheMatchingConnectedElectronSession()
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var id = state.CreateSession("test.electron", "Electron", IPAddress.Loopback, null, null);
        state.SetSessionDeviceProfile(id, new DeviceAppProfile {
            Sdk = new DeviceSdkProfile { PackageId = "@ansight/electron" },
            Device = new DeviceProfile { NativeDeviceId = "electron:test" }
        }, null);
        var bridge = new Bridge(id);
        var driver = new ElectronUiInputDriver(state, bridge, null);
        Assert.True(driver.GetAvailability("electron:test").IsAvailable);
        Assert.True((await driver.TapAsync(new(id, "electron:test", .25, .5))).IsSuccess);
        Assert.Equal(.25, bridge.Arguments?["x"]?.GetValue<double>());
        Assert.True(bridge.Arguments?["normalized"]?.GetValue<bool>());
        Assert.False((await driver.TapAsync(new("other", "electron:test", .25, .5))).IsSuccess);
        Assert.False((await driver.TapAsync(new(id, "electron:other", .25, .5))).IsSuccess);
        Assert.Equal(1, bridge.CallCount);
        bridge.Connected = false;
        Assert.False(driver.GetAvailability("electron:test").IsAvailable);
    }

    [Fact]
    public void ElectronDomIsNotClassifiedAsCapacitor()
    {
        var result = NavigationControllerCatalog.ResolveVisualTree(new JsonObject { ["adapter"] = "@ansight/electron", ["platform"] = "web" }, "dom.get_document");
        Assert.Equal("electron", Assert.Single(result).Framework);
    }

    [Fact]
    public async Task ElectronTargetHintUsesAttachedWindowInsteadOfSimulatorAccessibility()
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var id = state.CreateSession("test.electron", "Electron", IPAddress.Loopback, null, null);
        state.SetSessionDeviceProfile(id, new DeviceAppProfile {
            Sdk = new DeviceSdkProfile { PackageId = "@ansight/electron" },
            Device = new DeviceProfile { NativeDeviceId = "electron:test" }
        }, null);
        Assert.True(state.TryGetSessionSnapshot(id, out var session));
        var result = await LiveUiTreeCapture.CaptureForSelectorAsync(session!, new Bridge(id),
            new UiInputRouter(), "ansight_tap_ui", null,
            LiveUiSelector.Parse(new JsonObject { ["automationId"] = "toggle-language" }),
            CancellationToken.None, targetSource: "dom.get_document");
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("dom.get_document", result.Capture?.ToolId);
    }

    private sealed class Bridge(string id) : IAppToolBridge
    {
        public bool Connected = true;
        public int CallCount;
        public JsonObject? Arguments;
        public event EventHandler? ConnectionsChanged { add { } remove { } }
        public IReadOnlyList<string> GetConnectedSessionIds() => Connected ? [id] : [];
        public bool IsSessionConnected(string sessionId) => Connected && sessionId == id;
        public OperationResult ForceDisconnectSession(string sessionId) => OperationResult.Success("Disconnected");
        public Task<AppToolBridgeResponse> QueryToolsAsync(string sessionId, CancellationToken ct, AppToolBridgeRequestContext? context = null) => Task.FromResult(AppToolBridgeResponse.FromSuccess("Catalog", new ToolProtocolEnvelope {
            Type = "tool.catalog", Id = "catalog", Payload = new JsonObject { ["tools"] = new JsonArray(new JsonObject { ["id"] = "electron.input", ["policy"] = "write", ["executable"] = true }, new JsonObject { ["id"] = "ui.get_visual_tree", ["policy"] = "read", ["executable"] = true }, new JsonObject { ["id"] = "dom.get_document", ["policy"] = "read", ["executable"] = true }) }
        }));
        public Task<AppToolBridgeResponse> CallToolAsync(string sessionId, string toolId, JsonObject? args, CancellationToken ct, AppToolBridgeRequestContext? context = null)
        {
            if (toolId is "ui.get_visual_tree" or "dom.get_document")
            {
                var tree = JsonNode.Parse("""
                    {"format":"ansight.dom.visual-tree.compact.v2","types":["body","button"],"adapter":"@ansight/electron","platform":"web","viewport":{"width":800,"height":600},"root":{"id":"root","typeId":0,"bounds":{"x":0,"y":0,"width":800,"height":600},"children":[{"id":"button","automationId":"toggle-language","typeId":1,"role":"button","visible":true,"enabled":true,"bounds":{"x":10,"y":10,"width":100,"height":40}}]}}
                    """);
                return Task.FromResult(AppToolBridgeResponse.FromSuccess("Tree", new ToolProtocolEnvelope {
                    Type = "tool.result", Id = "tree", Payload = new JsonObject { ["success"] = true, ["result"] = tree }
                }));
            }
            Assert.Equal("electron.input", toolId);
            Arguments = args; CallCount++;
            return Task.FromResult(AppToolBridgeResponse.FromSuccess("Delivered", new ToolProtocolEnvelope {
                Type = "tool.result", Id = "result", Payload = new JsonObject { ["success"] = true, ["result"] = new JsonObject { ["performed"] = true } }
            }));
        }
    }
}
