using Ansight.Host.Tests.TestSupport;
using Ansight.Host.UiAutomation;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class AppInteractionBackendTests
{
    [Theory]
    [InlineData("unchanged", true, false)]
    [InlineData("moved", false, false)]
    [InlineData("replaced", false, false)]
    [InlineData("removed", false, false)]
    [InlineData("unchanged", true, true)]
    [InlineData("moved", false, true)]
    [InlineData("replaced", false, true)]
    [InlineData("removed", false, true)]
    public async Task DiscoveryHintTapsTheChosenDuplicateOnlyWhileItsObservedContextStillMatches(
        string change, bool shouldTap, bool typeText)
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var sessionId = state.CreateDeviceSession(new WorkspaceTestTarget(
            "ios", "simulator-001", "Phone", "test.app", false, false, true)
            { DeviceKind = DeviceKinds.Simulator, ExecutionMode = "device" });
        var bridge = new ScreenshotBridge("unrelated-session", string.Empty);
        var composition = new MefHostComposition(environment.ApplicationPaths,
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        using var dispatcher = new OperationDispatcher(state, environment.ApplicationPaths,
            composition.Get<IKnownAppStore>(), composition.Get<IPairingConfigService>(), composition.Get<IPairingConfigCache>(),
            composition.Get<AppService>(), composition.Get<PairingService>(), composition.Get<DotNetProfilingService>(),
            composition.Get<NativeProfilingService>(), bridge, externalSessionScreenshotCaptureManager: new StaticScreenshots());
        var input = new InputDriver();
        var accessibility = new DuplicateResultAccessibility();
        if (typeText)
        {
            foreach (var index in new[] { 1, 3 })
            {
                var field = accessibility.Tree["root"]!["children"]![index]!;
                field["typeId"] = 1;
                field["role"] = "textbox";
                field["supportedActions"] = new JsonArray("focus", "typeText");
            }
        }
        dispatcher.ConfigureUiInputDriver(input);
        dispatcher.ConfigureUiAccessibilityDriver(accessibility);
        try
        {
            var found = await dispatcher.CallToolAsync("ansight_find_ui", new JsonObject
            {
                ["sessionId"] = sessionId, ["text"] = "Eagle Rock", ["exact"] = true
            });
            Assert.False(found.Payload?["isError"]?.GetValue<bool>(), found.Payload?.ToJsonString());
            var matches = found.Payload!["structuredContent"]!["matches"]!.AsArray();
            Assert.Equal(3, matches.Count); // Search field plus two distinct rows.
            var selected = matches[2]!;
            Assert.Contains("New South Wales", selected["nearbyText"]!.ToJsonString());
            var arguments = selected["tapHint"]!["selector"]!.DeepClone().AsObject();
            Assert.Equal(typeText ? 2 : 1, arguments["index"]!.GetValue<int>());
            Assert.NotNull(arguments["targetFingerprint"]);
            arguments["sessionId"] = sessionId;
            arguments["includeScreenshot"] = false;
            if (typeText) arguments["value"] = "New search";

            var children = accessibility.Tree["root"]!["children"]!.AsArray();
            if (change == "moved") children[3]!["bounds"]![1] = 0.7;
            if (change == "replaced") children[4]!["text"] = "Victoria · Crag";
            if (change == "removed") children.RemoveAt(3);
            var capturesBeforeTap = accessibility.Captures;
            var tapped = await dispatcher.CallToolAsync(typeText ? "ansight_type_text" : "ansight_tap_ui", arguments);

            Assert.True(accessibility.Captures > capturesBeforeTap);
            Assert.False(accessibility.LastAllowCached);
            Assert.Equal(!shouldTap, tapped.Payload?["isError"]?.GetValue<bool>());
            Assert.Equal(shouldTap ? 1 : 0, input.Taps);
            if (typeText) Assert.Equal(shouldTap ? "New search" : null, input.TypedText);
            if (shouldTap)
            {
                Assert.Equal(0.4, input.LastTap!.NormalizedX, 6);
                Assert.Equal(0.525, input.LastTap.NormalizedY, 6);
            }
            else
            {
                Assert.False(tapped.Payload?["structuredContent"]?["performed"]?.GetValue<bool>());
                if (change != "removed")
                    Assert.Contains("fresh tapHint", tapped.Payload!["structuredContent"]!["message"]!.GetValue<string>());
            }
        }
        finally
        {
            state.EndDeviceSession(sessionId);
        }
    }

    private sealed class DuplicateResultAccessibility : IUiAccessibilityDriver
    {
        public JsonObject Tree { get; } = new()
        {
            ["format"] = "ansight.device-accessibility.compact.v2",
            ["types"] = new JsonArray("Application", "TextField", "StaticText"),
            ["coordinateSpace"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 1, ["height"] = 1 },
            ["root"] = new JsonObject
            {
                ["typeId"] = 0, ["flags"] = 3, ["bounds"] = new JsonArray(0, 0, 1, 1),
                ["children"] = new JsonArray(
                    Label("Eagle Rock", 0.05, "textbox"),
                    Label("Eagle Rock", 0.25), Label("Tasmania · Crag", 0.31),
                    Label("Eagle Rock", 0.50), Label("New South Wales · Crag", 0.56))
            }
        };
        public int Captures { get; private set; }
        public bool LastAllowCached { get; private set; }
        public Task<UiAccessibilityResult> CaptureAccessibilityAsync(UiAccessibilityRequest request,
            CancellationToken cancellationToken = default)
        {
            Captures++;
            LastAllowCached = request.AllowCached;
            return Task.FromResult(new UiAccessibilityResult(true, "test", "Captured.", Tree.DeepClone().AsObject()));
        }
        private static JsonObject Label(string text, double y, string role = "text") => new()
        {
            ["typeId"] = role == "text" ? 2 : 1, ["role"] = role,
            ["text"] = text, ["flags"] = 3, ["bounds"] = new JsonArray(0.1, y, 0.6, 0.05),
            ["supportedActions"] = new JsonArray()
        };
    }
}
