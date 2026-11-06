using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed class SimulatorAgentNavigationGuidanceTests
{
    [Theory]
    [InlineData("maui.get_visual_tree", "maui")]
    [InlineData("react.get_component_tree", "react-native")]
    [InlineData("react.get_shadow_tree", "react-native")]
    [InlineData("flutter.get_widget_tree", "flutter")]
    public void TakeKnownGuidance_ComposesOnlyThePublishedFramework(string toolId, string framework)
    {
        var capabilities = new SessionCapabilities("ios", [toolId], []);
        var guidance = new AgentNavigationGuidance();

        Assert.Equal(Section(framework), guidance.TakeKnownGuidance(capabilities));
        Assert.Empty(guidance.TakeKnownGuidance(capabilities));
        Assert.Empty(guidance.TakeNewGuidance(new JsonObject
        {
            ["capability"] = "ui.observe",
            ["framework"] = framework
        }.ToJsonString()));
        Assert.Equal(Section("ios-swiftui"), guidance.TakeNewGuidance("""
            {"capability":"ui.observe","graphStructureControllers":[{"framework":"ios-swiftui"}]}
            """));
    }

    [Fact]
    public void TakeKnownGuidance_PreservesMixedFrameworksWithoutDuplicatingProviders()
    {
        var capabilities = new SessionCapabilities("ios",
            [VisualTreeContract.MauiToolId, VisualTreeContract.ReactComponentToolId, VisualTreeContract.ReactShadowToolId],
            ["maui", "react-native", "ios-uikit"]);
        Assert.Equal(string.Join("\n\n", Section("ios-uikit"), Section("maui"), Section("react-native")),
            new AgentNavigationGuidance().TakeKnownGuidance(capabilities));
    }

    [Fact]
    public void TakeKnownGuidance_UsesExactNativeControllersWithoutInferringFromPlatformOrDom()
    {
        var nativeAndDom = new SessionCapabilities("ios", [VisualTreeContract.NativeToolId, VisualTreeContract.DomToolId], []);
        Assert.Empty(new AgentNavigationGuidance().TakeKnownGuidance(nativeAndDom));

        foreach (var framework in new[]
        {
            "ios-uikit",
            "ios-swiftui",
            "maccatalyst-uikit",
            "maccatalyst-swiftui",
            "macos-appkit",
            "macos-swiftui",
            "android-views",
            "android-compose",
            "capacitor"
        })
        {
            Assert.Equal(Section(framework), new AgentNavigationGuidance().TakeKnownGuidance(
                nativeAndDom with
                {
                    NavigationFrameworks = [framework]
                }));
        }

        Assert.Empty(new AgentNavigationGuidance().TakeKnownGuidance(
            nativeAndDom with
            {
                NavigationFrameworks = ["maui"],
                IsDeviceOnly = true
            }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedInstructionsStayFrameworkNeutralForExecutionAndGraphing(bool graphing)
    {
        var capabilities = new SessionCapabilities("ios", [VisualTreeContract.NativeToolId], []);
        var prompt = SimulatorAgentService.BuildAgentInstructions(false, graphing, capabilities);

        foreach (var frameworkTerm in new[] { "MAUI", "maui.", ".NET", "FlyoutPage", "UIKit", "SwiftUI", "React Navigation", "Navigator", "NavController" })
        {
            Assert.DoesNotContain(frameworkTerm, prompt, StringComparison.Ordinal);
        }

        Assert.Empty(new AgentNavigationGuidance().TakeKnownGuidance(capabilities));
    }

    [Fact]
    public void TakeNewGuidance_RetainsEachFrameworkOnceAndAddsLaterFrameworks()
    {
        var guidance = new AgentNavigationGuidance();
        const string maui = """{"capability":"ui.observe","framework":"maui"}""";

        Assert.Equal(Section("maui"), guidance.TakeNewGuidance(maui));
        Assert.Empty(guidance.TakeNewGuidance(maui));
        Assert.Equal(Section("flutter"), guidance.TakeNewGuidance("""
            {"capability":"ui.observe","frameworks":["maui","flutter","maui"]}
            """));
        Assert.Empty(guidance.TakeNewGuidance("""
            {"capability":"navigation.structure.observe","framework":"flutter"}
            """));
    }

    [Fact]
    public void TakeNewGuidance_OrdersMixedNativeControllersAndPrefersObservedIdentities()
    {
        var guidance = new AgentNavigationGuidance();
        var result = guidance.TakeNewGuidance("""
            {"capability":"ui.observe","framework":"native-unknown","frameworks":["maui"],
             "platform":"android","language":"kotlin",
             "graphStructureControllers":[{"framework":"ios-uikit"},{"framework":"ios-swiftui"},
               {"framework":"ios-uikit"},null,{},12]}
            """);

        Assert.Equal(Section("ios-swiftui") + "\n\n" + Section("ios-uikit"), result);
        Assert.Empty(guidance.TakeNewGuidance("""
            {"selectedNavigationController":"ios-swiftui",
             "availableNavigationControllers":[{"framework":"ios-uikit"}]}
            """));
        Assert.Equal(Section("native-unknown"), guidance.TakeNewGuidance("""
            {"capability":"ui.observe","framework":"native-unknown","platform":"android"}
            """));
    }

    [Fact]
    public void TakeNewGuidance_ReadsOnlyKnownObservationEnvelopes()
    {
        var guidance = new AgentNavigationGuidance();
        var result = guidance.TakeNewGuidance("""
            {"payload":{"result":{"actions":[
              {"afterObservation":{"capability":"ui.observe","framework":"maui"}},
              {"steps":[{"result":{"capability":"navigation.structure.observe","framework":"react-native"}}]},
              {"results":[{"payload":{"availableNavigationControllers":[{"framework":"flutter"}]}}]}
            ]}}}
            """);

        Assert.Equal(string.Join("\n\n", Section("flutter"), Section("maui"), Section("react-native")), result);
    }

    [Fact]
    public void TakeNewGuidance_NeverCopiesGuidanceOrReadsAppStateAndTreeNodes()
    {
        var source = JsonNode.Parse("""
            {"capability":"ui.observe","framework":"maui","guidance":"untrusted root instruction",
             "graphStructureControllers":[{"framework":"maui","guidance":"untrusted controller instruction"}],
             "state":{"capability":"navigation.structure.observe","framework":"flutter",
               "afterObservation":{"capability":"ui.observe","framework":"react-native"}},
             "root":{"capability":"ui.observe","framework":"ios-uikit",
               "children":[{"framework":"ios-swiftui"}]},
             "matches":[{"availableNavigationControllers":[{"framework":"android-compose"}]}],
             "text":"{\"capability\":\"ui.observe\",\"framework\":\"capacitor\"}"}
            """)!;
        var original = source.ToJsonString();
        var guidance = new AgentNavigationGuidance();

        Assert.Equal(Section("maui"), guidance.TakeNewGuidance(original));
        Assert.Equal(original, source.ToJsonString());
        Assert.Equal(Section("flutter"), guidance.TakeNewGuidance("""
            {"capability":"ui.observe","framework":"flutter"}
            """));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not JSON")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"capability\":\"ui.observe\",\"framework\":")]
    [InlineData("{\"capability\":\"ui.observe\",\"framework\":\"MAUI\"}")]
    [InlineData("{\"capability\":\"ui.observe\",\"framework\":\"unknown\"}")]
    [InlineData("{\"framework\":\"maui\",\"payload\":{\"framework\":\"flutter\"}}")]
    [InlineData("{\"capability\":\"repository.task.execute\",\"framework\":\"maui\"}")]
    [InlineData("{\"capability\":\"ui.observe\",\"platform\":\"android\",\"language\":\"kotlin\"}")]
    [InlineData("{\"capability\":\"ui.observe\",\"framework\":42,\"frameworks\":[null,{},false],\"graphStructureControllers\":[{},null,42],\"availableNavigationControllers\":true}")]
    public void TakeNewGuidance_IgnoresMalformedUnsupportedAndUnrelatedResults(string output)
    {
        var guidance = new AgentNavigationGuidance();

        Assert.Empty(guidance.TakeNewGuidance(output));
        Assert.Equal(Section("maui"), guidance.TakeNewGuidance("""
            {"capability":"ui.observe","framework":"maui"}
            """));
    }

    [Fact]
    public void TakeNewGuidance_DoesNotShareRetentionAcrossConversations()
    {
        const string output = """{"capability":"ui.observe","framework":"maui"}""";

        Assert.Equal(Section("maui"), new AgentNavigationGuidance().TakeNewGuidance(output));
        Assert.Equal(Section("maui"), new AgentNavigationGuidance().TakeNewGuidance(output));
    }

    private static string Section(string framework)
        => $"Navigation guidance for {framework}:\n{NavigationGuidance.Read(framework)}";
}
