using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed class SimulatorAgentNavigationGuidanceTests
{
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
