using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class UiResultProjectionTests
{
    [Fact]
    public void WaitResult_SharesDeepAncestryAndKeepsVerdictCountsAndSourceUnchanged()
    {
        var path = new JsonArray(Enumerable.Range(0, 8).Select(index => (JsonNode?)new JsonObject
        {
            ["id"] = "ancestor-" + index,
            ["automationId"] = "container-" + index,
            ["text"] = "Container " + index,
            ["debug"] = new string('x', 1_000)
        }).ToArray());
        var source = new JsonObject
        {
            ["capability"] = "ui.wait_for",
            ["condition"] = "visible",
            ["satisfied"] = true,
            ["matchCount"] = 2,
            ["selector"] = new JsonObject { ["text"] = "Ready" },
            ["matches"] = new JsonArray(Node("first", path), Node("second", path)),
            ["attempts"] = 3,
            ["treeHash"] = "retained-evidence-hash"
        };
        var original = source.ToJsonString();

        var result = UiResultProjection.Project(source, UiProjectionOptions.Model);

        Assert.True(result["satisfied"]!.GetValue<bool>());
        Assert.Equal(2, result["matchCount"]!.GetValue<int>());
        Assert.Equal(3, result["attempts"]!.GetValue<int>());
        Assert.Equal("retained-evidence-hash", result["treeHash"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(source["selector"], result["selector"]));
        var ancestors = Assert.IsType<JsonArray>(result["ancestors"]);
        Assert.Equal(3, ancestors.Count);
        Assert.Equal("ancestor-5", ancestors[0]!["id"]!.GetValue<string>());
        foreach (var node in result["matches"]!.AsArray().OfType<JsonObject>())
        {
            Assert.False(node.ContainsKey("ancestorPath"));
            Assert.Equal("ancestor-7", node["parentId"]!.GetValue<string>());
            Assert.True(node["ancestorPathTruncated"]!.GetValue<bool>());
            Assert.Null(node["visual"]);
            Assert.Null(node["debug"]);
            Assert.Equal("[10,20,30,40]", node["bounds"]!.ToJsonString());
        }
        Assert.True(result["projectionTruncated"]!.GetValue<bool>());
        Assert.Equal(original, source.ToJsonString());
    }

    [Fact]
    public void AssertAndActionResults_PreserveExactFailuresTargetsAndEndpointEvidence()
    {
        var exactError = "Expected exact value: " + new string('e', 600);
        var source = new JsonObject
        {
            ["isError"] = true,
            ["result"] = new JsonObject
            {
                ["capability"] = "ui.sequence",
                ["performed"] = false,
                ["completedActionCount"] = 1,
                ["failureCount"] = 1,
                ["evidence"] = new JsonObject { ["before"] = "before-hash", ["after"] = "after-hash" },
                ["actions"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["performed"] = true,
                        ["target"] = Node("tapped"),
                        ["afterObservation"] = new JsonObject
                        {
                            ["root"] = new JsonObject { ["id"] = "already-projected", ["children"] = new JsonArray() }
                        }
                    },
                    new JsonObject
                    {
                        ["passed"] = false,
                        ["selected"] = Node("asserted"),
                        ["failures"] = new JsonArray(exactError),
                        ["error"] = exactError
                    }
                }
            }
        };
        var original = source.ToJsonString();

        var result = UiResultProjection.Project(source, UiProjectionOptions.Model);

        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.False(result["result"]!["performed"]!.GetValue<bool>());
        var actions = result["result"]!["actions"]!.AsArray();
        Assert.True(actions[0]!["performed"]!.GetValue<bool>());
        Assert.False(actions[1]!["passed"]!.GetValue<bool>());
        Assert.Equal(exactError, actions[1]!["error"]!.GetValue<string>());
        Assert.Equal(exactError, actions[1]!["failures"]![0]!.GetValue<string>());
        Assert.Null(actions[0]!["target"]!["visual"]);
        Assert.Null(actions[1]!["selected"]!["visual"]);
        Assert.Equal("tapped", actions[0]!["target"]!["id"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(source["result"]!["evidence"], result["result"]!["evidence"]));
        Assert.True(JsonNode.DeepEquals(source["result"]!["actions"]![0]!["afterObservation"], actions[0]!["afterObservation"]));
        Assert.Equal(original, source.ToJsonString());
    }

    [Fact]
    public void IdlessAncestors_AreBoundedWithoutMergingIdenticalLabels()
    {
        var path = new JsonArray(new JsonObject { ["automationId"] = "page" },
            new JsonObject { ["automationId"] = "duplicated-row", ["text"] = "Repeated label" });
        var source = new JsonObject
        {
            ["matches"] = new JsonArray(Node("one", path), Node("two", path))
        };

        var result = UiResultProjection.Project(source, UiProjectionOptions.Model with { MaximumAncestors = 1 });

        Assert.Null(result["ancestors"]);
        foreach (var node in result["matches"]!.AsArray().OfType<JsonObject>())
        {
            Assert.Single(node["ancestorPath"]!.AsArray());
            Assert.Null(node["parentId"]);
            Assert.True(node["ancestorPathTruncated"]!.GetValue<bool>());
        }
    }

    [Fact]
    public void DisplayTruncation_NeverShortensIdentityOrSuppliedSelectorAndPreservesNullHints()
    {
        var identity = "exact-" + new string('i', 800);
        var text = "exact target " + new string('t', 600);
        var source = Node(identity);
        source["automationId"] = identity;
        source["type"] = identity;
        source["text"] = text;
        source["value"] = text;
        source["tapHint"] = new JsonObject
        {
            ["tool"] = "ansight_tap_ui",
            ["selector"] = new JsonObject { ["automationId"] = identity, ["text"] = text }
        };

        var result = UiNodeProjection.FromResult(source, UiProjectionOptions.Model);

        Assert.Equal(identity, result["id"]!.GetValue<string>());
        Assert.Equal(identity, result["automationId"]!.GetValue<string>());
        Assert.Equal(identity, result["type"]!.GetValue<string>());
        Assert.Equal(240, result["text"]!.GetValue<string>().Length);
        Assert.True(result["textTruncated"]!.GetValue<bool>());
        Assert.True(result["valueTruncated"]!.GetValue<bool>());
        Assert.True(JsonNode.DeepEquals(source["tapHint"], result["tapHint"]));
        var exact = UiNodeProjection.FromResult(source, UiProjectionOptions.Interaction);
        Assert.Equal(text, exact["text"]!.GetValue<string>());
        Assert.Equal(text, exact["value"]!.GetValue<string>());
        Assert.Null(exact["textTruncated"]);

        source["tapHint"] = null;
        var refused = UiNodeProjection.FromResult(source, UiProjectionOptions.Model);
        Assert.True(refused.ContainsKey("tapHint"));
        Assert.Null(refused["tapHint"]);
        source.Remove("tapHint");
        Assert.False(UiNodeProjection.FromResult(source, UiProjectionOptions.Model).ContainsKey("tapHint"));
    }

    [Fact]
    public void BoundsPacking_IsIdempotentAndPreservesExtendedCoordinateSpaces()
    {
        var source = new JsonObject { ["x"] = 1.25, ["y"] = 2, ["width"] = 3, ["height"] = 4 };
        var compact = UiNodeProjection.CompactBounds(source);
        Assert.Equal("[1.25,2,3,4]", compact.ToJsonString());
        Assert.True(JsonNode.DeepEquals(compact, UiNodeProjection.CompactBounds(compact)));
        Assert.IsType<JsonObject>(source);
        source["coordinateSpace"] = "viewport";
        Assert.True(JsonNode.DeepEquals(source, UiNodeProjection.CompactBounds(source)));
    }

    [Fact]
    public void Budget_CountsSharedAncestorBytesAndKeepsAuthoritativeMatchCount()
    {
        var path = new JsonArray(new JsonObject { ["id"] = "ancestor", ["text"] = new string('a', 200) });
        var source = new JsonObject
        {
            ["satisfied"] = true,
            ["matchCount"] = 5,
            ["matches"] = new JsonArray(Enumerable.Range(0, 5).Select(index => (JsonNode?)Node("node-" + index, path)).ToArray())
        };

        var result = UiResultProjection.Project(source, UiProjectionOptions.Model with { MaximumCharacters = 700 });

        Assert.True(result["satisfied"]!.GetValue<bool>());
        Assert.Equal(5, result["matchCount"]!.GetValue<int>());
        Assert.InRange(result["matches"]!.AsArray().Count, 1, 4);
        Assert.True(result["projectionTruncated"]!.GetValue<bool>());
        Assert.Equal(5, result["projectionSourceNodeCount"]!.GetValue<int>());
        var projectedNodes = new JsonObject
        {
            ["matches"] = result["matches"]!.DeepClone(),
            ["ancestors"] = result["ancestors"]!.DeepClone()
        };
        Assert.True(projectedNodes.ToJsonString().Length <= 700);
    }

    [Fact]
    public void OversizedIdentity_OmitsMatchWithCountsButRetainsVerdictTargetAndFlagsOverrun()
    {
        var identity = new string('i', 1_000);
        var options = UiProjectionOptions.Model with { MaximumCharacters = 100 };
        var matches = UiResultProjection.Project(new JsonObject
        {
            ["matchCount"] = 1,
            ["matches"] = new JsonArray(Node(identity))
        }, options);
        Assert.Empty(matches["matches"]!.AsArray());
        Assert.Equal(1, matches["matchCount"]!.GetValue<int>());
        Assert.True(matches["projectionTruncated"]!.GetValue<bool>());

        var action = UiResultProjection.Project(new JsonObject
        {
            ["performed"] = true,
            ["target"] = Node(identity)
        }, options);
        Assert.True(action["performed"]!.GetValue<bool>());
        Assert.Equal(identity, action["target"]!["id"]!.GetValue<string>());
        Assert.True(action["projectionBudgetExceeded"]!.GetValue<bool>());
    }

    [Fact]
    public void FromMatch_UsesEffectiveStateAndRegistryWithoutMutatingCanonicalTree()
    {
        var root = new JsonObject
        {
            ["id"] = "root", ["typeId"] = 0, ["enabled"] = false, ["visible"] = true,
            ["children"] = new JsonArray(new JsonObject
            {
                ["id"] = "field", ["typeId"] = 1, ["automationId"] = "field-id", ["text"] = "Label",
                ["value"] = "Current value", ["enabled"] = true, ["visible"] = true,
                ["bounds"] = new JsonArray(1, 2, 3, 4), ["debug"] = new string('x', 2_000)
            })
        };
        var registry = VisualTreeTypeRegistry.FromPayload(new JsonObject { ["types"] = new JsonArray("Page", "Entry") });
        var original = root.ToJsonString();
        var match = LiveUiNodeQuery.Enumerate(root, registry).ElementAt(1);

        var result = UiNodeProjection.FromMatch(match, UiProjectionOptions.Model);

        Assert.Equal("Entry", result["type"]!.GetValue<string>());
        Assert.False(result["enabled"]!.GetValue<bool>());
        Assert.Equal("Current value", result["value"]!.GetValue<string>());
        Assert.Equal("root", result["ancestorPath"]![0]!["id"]!.GetValue<string>());
        Assert.Null(result["debug"]);
        Assert.Equal(original, root.ToJsonString());
    }

    [Fact]
    public void ExistingAncestorTable_RetainsOnlyReachableAllowlistedStateAfterMatchTruncation()
    {
        var source = new JsonObject
        {
            ["matches"] = new JsonArray(
                new JsonObject { ["id"] = "one", ["parentId"] = "parent-one" },
                new JsonObject { ["id"] = "two", ["parentId"] = "parent-two" }),
            ["ancestors"] = new JsonArray
            {
                new JsonObject { ["id"] = "parent-one", ["parentId"] = "root", ["debug"] = new string('x', 20_000) },
                new JsonObject { ["id"] = "parent-two", ["parentId"] = "root" },
                new JsonObject { ["id"] = "root", ["automationId"] = "page" },
                new JsonObject { ["id"] = "unreachable", ["debug"] = new string('x', 20_000) }
            }
        };

        var result = UiResultProjection.Project(source, UiProjectionOptions.Model with { MaximumMatches = 1 });

        Assert.Single(result["matches"]!.AsArray());
        var ancestors = result["ancestors"]!.AsArray();
        Assert.Equal(2, ancestors.Count);
        Assert.Equal(["parent-one", "root"], ancestors.OfType<JsonObject>().Select(node => node["id"]!.GetValue<string>()));
        Assert.DoesNotContain("debug", result.ToJsonString(), StringComparison.Ordinal);
        Assert.True(result["projectionTruncated"]!.GetValue<bool>());
        Assert.Null(result["projectionBudgetExceeded"]);
    }

    [Fact]
    public void RecursiveBoundsPacking_PreservesViewportAndDoesNotPromoteNestedSecureValues()
    {
        var bounds = new JsonObject { ["x"] = 1, ["y"] = 2, ["width"] = 3, ["height"] = 4 };
        var source = new JsonObject
        {
            ["viewport"] = bounds.DeepClone(),
            ["afterObservation"] = new JsonObject { ["root"] = new JsonObject { ["bounds"] = bounds.DeepClone() } },
            ["target"] = new JsonObject
            {
                ["id"] = "secure-field", ["isPassword"] = true,
                ["visual"] = new JsonObject { ["value"] = "do-not-promote" }
            }
        };

        var result = UiResultProjection.Project(source, UiProjectionOptions.Model);

        Assert.True(JsonNode.DeepEquals(bounds, result["viewport"]));
        Assert.Equal("[1,2,3,4]", result["afterObservation"]!["root"]!["bounds"]!.ToJsonString());
        Assert.Null(result["target"]!["value"]);
        Assert.True(result["target"]!["isPassword"]!.GetValue<bool>());
        Assert.DoesNotContain("do-not-promote", result.ToJsonString(), StringComparison.Ordinal);
        Assert.IsType<JsonObject>(source["afterObservation"]!["root"]!["bounds"]);
    }

    [Fact]
    public void CompactPageFlags_AreDecodedWithExplicitStatePrecedence()
    {
        var registry = VisualTreeTypeRegistry.FromPayload(new JsonObject
        {
            ["types"] = new JsonArray("ContentPage", "Button")
        });
        var node = new JsonObject { ["id"] = "page", ["typeId"] = 0, ["flags"] = 15 };
        var projected = UiNodeProjection.FromMatch(LiveUiNodeQuery.Enumerate(node, registry).Single(), UiProjectionOptions.Model);
        Assert.True(projected["currentPage"]!.GetValue<bool>());
        Assert.True(projected["activePage"]!.GetValue<bool>());

        node["currentPage"] = false;
        node["activePage"] = false;
        projected = UiNodeProjection.FromMatch(LiveUiNodeQuery.Enumerate(node, registry).Single(), UiProjectionOptions.Model);
        Assert.False(projected["currentPage"]!.GetValue<bool>());
        Assert.False(projected["activePage"]!.GetValue<bool>());
        Assert.False(UiNodeProjection.FromResult(node, UiProjectionOptions.Model)["activePage"]!.GetValue<bool>());

        node.Remove("currentPage");
        node.Remove("activePage");
        node["flags"] = 3;
        projected = UiNodeProjection.FromMatch(LiveUiNodeQuery.Enumerate(node, registry).Single(), UiProjectionOptions.Model);
        Assert.False(projected["currentPage"]!.GetValue<bool>());
        Assert.False(projected["activePage"]!.GetValue<bool>());
        node["typeId"] = 1;
        projected = UiNodeProjection.FromMatch(LiveUiNodeQuery.Enumerate(node, registry).Single(), UiProjectionOptions.Model);
        Assert.Null(projected["currentPage"]);
        Assert.Null(projected["activePage"]);
    }

    [Fact]
    public void AnonymousWrappers_DoNotDisplaceMeaningfulStableAncestorContext()
    {
        var source = Node("target", new JsonArray
        {
            new JsonObject { ["id"] = "page", ["automationId"] = "stable-page" },
            new JsonObject { ["id"] = "container", ["automationId"] = "stable-container" },
            new JsonObject { ["id"] = "wrapper-one", ["type"] = "Grid" },
            new JsonObject { ["id"] = "wrapper-two", ["type"] = "Grid" },
            new JsonObject { ["id"] = "wrapper-three", ["type"] = "Grid" },
            new JsonObject { ["id"] = "wrapper-four", ["type"] = "Grid" }
        });

        var result = UiNodeProjection.FromResult(source, UiProjectionOptions.Model);

        var ancestors = result["ancestorPath"]!.AsArray();
        Assert.Equal(["stable-page", "stable-container"], ancestors.OfType<JsonObject>().Select(ancestor => ancestor["automationId"]!.GetValue<string>()));
        Assert.True(result["ancestorPathTruncated"]!.GetValue<bool>());
        source["ancestorPath"] = new JsonArray(
            new JsonObject { ["id"] = "unique-one", ["type"] = "Grid" },
            new JsonObject { ["id"] = "unique-two", ["type"] = "Grid" });
        result = UiNodeProjection.FromResult(source, UiProjectionOptions.Model with { MaximumAncestors = 1 });
        Assert.Equal("unique-two", Assert.Single(result["ancestorPath"]!.AsArray())!["id"]!.GetValue<string>());
    }

    [Fact]
    public void InteractionProfile_BoundsNodesAndInlineAncestorsWithoutShorteningActionableText()
    {
        var text = new string('t', 800);
        var path = new JsonArray(new JsonObject { ["id"] = "parent", ["automationId"] = "stable-parent" });
        var source = new JsonObject
        {
            ["matches"] = new JsonArray(Enumerable.Range(0, 40).Select(index =>
            {
                var node = Node("node-" + index, path);
                node["text"] = text;
                return (JsonNode?)node;
            }).ToArray())
        };

        var result = UiResultProjection.Project(source, UiProjectionOptions.Interaction);

        Assert.InRange(result["matches"]!.AsArray().Count, 1, 39);
        Assert.Equal(40, result["projectionSourceNodeCount"]!.GetValue<int>());
        Assert.True(result["projectionTruncated"]!.GetValue<bool>());
        Assert.Null(result["ancestors"]);
        foreach (var node in result["matches"]!.AsArray().OfType<JsonObject>())
        {
            Assert.Equal(text, node["text"]!.GetValue<string>());
            Assert.Null(node["textTruncated"]);
            Assert.Equal("stable-parent", node["ancestorPath"]![0]!["automationId"]!.GetValue<string>());
        }
        Assert.True(new JsonObject { ["matches"] = result["matches"]!.DeepClone() }.ToJsonString().Length <= 16_000);
    }

    private static JsonObject Node(string id, JsonArray? ancestors = null)
        => new()
        {
            ["id"] = id,
            ["text"] = "Ready",
            ["role"] = "button",
            ["enabled"] = true,
            ["visible"] = true,
            ["supportedActions"] = new JsonArray("tap"),
            ["bounds"] = new JsonObject { ["x"] = 10, ["y"] = 20, ["width"] = 30, ["height"] = 40 },
            ["ancestorPath"] = ancestors?.DeepClone(),
            ["debug"] = new string('x', 1_000),
            ["visual"] = new JsonObject { ["style"] = new string('x', 1_000) }
        };
}
