using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed class SimulatorAgentObservationCompactionTests
{
    [Fact]
    public void UiResultProjection_RemovesVerboseVisualStateAndBoundsMatchCount()
    {
        var matches = new JsonArray();
        for (var index = 0; index < 30; index++)
        {
            matches.Add(new JsonObject
            {
                ["id"] = $"node-{index}",
                ["automationId"] = $"button-{index}",
                ["text"] = $"Button {index}",
                ["type"] = "Button",
                ["role"] = "button",
                ["visual"] = new JsonObject { ["debug"] = new string('x', 2_000) },
                ["ancestorPath"] = new JsonArray
                {
                    new JsonObject { ["automationId"] = "page" },
                    new JsonObject { ["automationId"] = "panel" },
                    new JsonObject { ["automationId"] = "row" }
                }
            });
        }

        var result = UiResultProjection.Project(new JsonObject
        {
            ["capability"] = "ui.find",
            ["matches"] = matches,
            ["totalMatches"] = 30
        }, UiProjectionOptions.Model);

        var compactMatches = Assert.IsType<JsonArray>(result["matches"]);
        Assert.InRange(compactMatches.Count, 1, 12);
        Assert.Equal(30, result["totalMatches"]?.GetValue<int>());
        Assert.True(result["projectionTruncated"]?.GetValue<bool>());
        Assert.DoesNotContain("visual", result.ToJsonString(), StringComparison.Ordinal);
        Assert.True(result.ToJsonString().Length < 6_000);
    }

    [Fact]
    public void UiResultProjection_PreservesAuthoritativeAncestorTapHintForTextLabel()
    {
        var result = UiResultProjection.Project(new JsonObject
        {
            ["capability"] = "ui.find",
            ["matches"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "open-guide-label",
                    ["text"] = "Open 3D Guide",
                    ["type"] = "Label",
                    ["tapHint"] = new JsonObject { ["tool"] = "ansight_tap_ui",
                        ["selector"] = new JsonObject { ["automationId"] = "area-open3d-guide-button" } },
                    ["role"] = "text",
                    ["bounds"] = new JsonObject
                    {
                        ["x"] = 487,
                        ["y"] = 1255,
                        ["width"] = 105,
                        ["height"] = 20
                    },
                    ["ancestorPath"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["automationId"] = "AreaPage",
                            ["type"] = "AreaPage"
                        },
                        new JsonObject
                        {
                            ["automationId"] = "area-open3d-guide-button",
                            ["type"] = "Border"
                        }
                    }
                }
            },
            ["totalMatches"] = 1
        }, UiProjectionOptions.Model);

        var match = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(result["matches"])[0]);
        Assert.Equal("ansight_tap_ui", match["tapHint"]?["tool"]?.GetValue<string>());
        Assert.Equal(
            "area-open3d-guide-button",
            match["tapHint"]?["selector"]?["automationId"]?.GetValue<string>());
    }

    [Fact]
    public void UiResultProjection_PreservesFuzzyEvidenceAndExactTapHint()
    {
        var result = UiResultProjection.Project(new JsonObject
        {
            ["capability"] = "ui.find",
            ["matches"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "area-siurana",
                    ["automationId"] = "area-result-siurana",
                    ["text"] = "Siurana",
                    ["type"] = "Button",
                    ["role"] = "button",
                    ["supportedActions"] = new JsonArray("tap"),
                    ["tapHint"] = new JsonObject { ["tool"] = "ansight_tap_ui",
                        ["selector"] = new JsonObject { ["automationId"] = "area-result-siurana" } },
                    ["matchMode"] = "fuzzy",
                    ["matchScore"] = 0.857,
                    ["matchReason"] = "text:token-edit-distance-1"
                }
            },
            ["totalMatches"] = 1
        }, UiProjectionOptions.Model);

        var match = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(result["matches"])[0]);
        Assert.Equal("fuzzy", match["matchMode"]?.GetValue<string>());
        Assert.Equal(0.857, match["matchScore"]?.GetValue<double>());
        Assert.Equal(
            "area-result-siurana",
            match["tapHint"]?["selector"]?["automationId"]?.GetValue<string>());
        Assert.Null(match["tapHint"]?["selector"]?["matchMode"]);
    }

    [Fact]
    public void AppToolObservation_PreservesMapLabelsWithoutStyleNoise()
    {
        var result = AppToolObservation.Build(new JsonObject
        {
            ["sessionId"] = "session-1",
            ["appId"] = "com.example.app",
            ["toolId"] = "com.example.maps.query_surface_contents",
            ["payload"] = new JsonObject
            {
                ["success"] = true,
                ["result"] = new JsonObject
                {
                    ["surface"] = new JsonObject
                    {
                        ["surfaceId"] = "1",
                        ["camera"] = new JsonObject { ["zoom"] = 14.5 }
                    },
                    ["annotationCount"] = 1,
                    ["annotationManagers"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "Areas",
                            ["annotations"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["id"] = "secret-garden",
                                    ["kind"] = "point",
                                    ["properties"] = new JsonObject
                                    {
                                        ["text-field"] = "Target Area",
                                        ["large-style-debug"] = new string('x', 10_000)
                                    }
                                }
                            }
                        }
                    }
                }
            }
        });

        var json = result.ToJsonString();
        Assert.Contains("Target Area", json, StringComparison.Ordinal);
        Assert.Contains("\"zoom\":14.5", json, StringComparison.Ordinal);
        Assert.Contains("\"projectionReturnedAnnotationCount\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"projectionTruncated\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"projectionSourceCount\":1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("large-style-debug", json, StringComparison.Ordinal);
        Assert.True(json.Length < 2_000);
    }

    [Fact]
    public void AppToolObservation_Preserves3dCameraEvidenceWithoutRendererInternals()
    {
        var result = AppToolObservation.Build(new JsonObject
        {
            ["toolId"] = "com.example.scene.query_view",
            ["payload"] = new JsonObject
            {
                ["success"] = true,
                ["result"] = new JsonObject
                {
                    ["player"] = new JsonObject
                    {
                        ["playerId"] = "1",
                        ["visible"] = true,
                        ["guide"] = new JsonObject
                        {
                            ["counts"] = new JsonObject { ["routes"] = 58 },
                            ["camera"] = new JsonObject
                            {
                                ["state"] = new JsonObject
                                {
                                    ["position"] = new JsonObject { ["x"] = 12.5 }
                                },
                                ["camera3D"] = new JsonObject
                                {
                                    ["properties"] = new string('x', 10_000)
                                }
                            }
                        }
                    }
                }
            }
        });

        var json = result.ToJsonString();
        Assert.Contains("\"routes\":58", json, StringComparison.Ordinal);
        Assert.Contains("\"x\":12.5", json, StringComparison.Ordinal);
        Assert.DoesNotContain("camera3D", json, StringComparison.Ordinal);
        Assert.True(json.Length < 2_000);
    }

    [Fact]
    public void AppToolObservation_AddsBoundedTapHintForOnScreenMapAnnotation()
    {
        var result = AppToolObservation.Build(new JsonObject
        {
            ["toolId"] = "com.example.maps.query_surface_contents",
            ["payload"] = new JsonObject
            {
                ["success"] = true,
                ["result"] = new JsonObject
                {
                    ["surface"] = new JsonObject
                    {
                        ["type"] = "Example.App.MapSurface",
                        ["visible"] = true,
                        ["enabled"] = true
                    },
                    ["annotationManagers"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["annotations"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["id"] = "secret-garden",
                                    ["screenPosition"] = new JsonObject
                                    {
                                        ["x"] = 530.1547,
                                        ["y"] = 690.9604
                                    },
                                    ["properties"] = new JsonObject
                                    {
                                        ["text-field"] = "Target Area"
                                    }
                                }
                            }
                        }
                    }
                }
            }
        });

        var annotation = result["payload"]?["result"]?["annotationManagers"]?[0]?["annotations"]?[0];
        Assert.Equal("ansight_tap_ui", annotation?["tapHint"]?["tool"]?.GetValue<string>());
        Assert.Equal("MapSurface", annotation?["tapHint"]?["selector"]?["type"]?.GetValue<string>());
        Assert.Equal(530.1547, annotation?["tapHint"]?["targetX"]?.GetValue<double>());
        Assert.Equal(690.9604, annotation?["tapHint"]?["targetY"]?.GetValue<double>());
    }

    [Fact]
    public void AppToolObservation_AddsSwipeHintForVisible3dPlayer()
    {
        var result = AppToolObservation.Build(new JsonObject
        {
            ["toolId"] = "com.example.scene.query_view",
            ["payload"] = new JsonObject
            {
                ["success"] = true,
                ["result"] = new JsonObject
                {
                    ["player"] = new JsonObject
                    {
                        ["type"] = "Example.App.SceneView",
                        ["ownerTag"] = "ScenePage",
                        ["visible"] = true,
                        ["enabled"] = true
                    }
                }
            }
        });

        var swipeHint = result["payload"]?["result"]?["player"]?["swipeHint"];
        Assert.Equal("ansight_swipe_ui", swipeHint?["tool"]?.GetValue<string>());
        Assert.Equal("SceneView", swipeHint?["selector"]?["type"]?.GetValue<string>());
        Assert.Equal(
            "ScenePage",
            swipeHint?["selector"]?["ancestorAutomationId"]?.GetValue<string>());
        Assert.Equal("W", swipeHint?["orientation"]?.GetValue<string>());
        Assert.Equal(0.35, swipeHint?["length"]?.GetValue<double>());
        Assert.Equal(400, swipeHint?["durationMs"]?.GetValue<int>());
    }

    [Fact]
    public void AppToolObservation_ReportsWhenItsLocalAnnotationProjectionIsTruncated()
    {
        var annotations = new JsonArray();
        for (var index = 0; index < 100; index++)
        {
            annotations.Add(new JsonObject
            {
                ["id"] = $"annotation-{index}-{new string('x', 80)}",
                ["kind"] = "point",
                ["properties"] = new JsonObject { ["text-field"] = $"Area {index}" }
            });
        }

        var result = AppToolObservation.Build(new JsonObject
        {
            ["toolId"] = "com.example.maps.query_surface_contents",
            ["payload"] = new JsonObject
            {
                ["success"] = true,
                ["result"] = new JsonObject
                {
                    ["returnedAnnotationCount"] = 100,
                    ["truncated"] = false,
                    ["annotationManagers"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "Areas",
                            ["returnedCount"] = 100,
                            ["annotations"] = annotations
                        }
                    }
                }
            }
        });

        Assert.True(result["payload"]?["result"]?["projectionTruncated"]?.GetValue<bool>());
        Assert.True(
            result["payload"]?["result"]?["projectionReturnedAnnotationCount"]?.GetValue<int>() < 100);
        Assert.False(result["payload"]?["result"]?["truncated"]?.GetValue<bool>());
    }

    [Fact]
    public void AppToolObservation_PreservesCompleteThirtyPointProjection()
    {
        var annotations = new JsonArray();
        for (var index = 0; index < 30; index++)
        {
            annotations.Add(new JsonObject
            {
                ["id"] = $"00000000-0000-0000-0000-{index:000000000000}",
                ["kind"] = "point",
                ["screenPosition"] = new JsonObject { ["x"] = index, ["y"] = index },
                ["selected"] = false,
                ["properties"] = new JsonObject { ["text-field"] = $"Area {index}" }
            });
        }

        var result = AppToolObservation.Build(new JsonObject
        {
            ["toolId"] = "com.example.maps.query_surface_contents",
            ["payload"] = new JsonObject
            {
                ["success"] = true,
                ["result"] = new JsonObject
                {
                    ["returnedAnnotationCount"] = 30,
                    ["truncated"] = false,
                    ["annotationManagers"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "Areas",
                            ["returnedCount"] = 30,
                            ["annotations"] = annotations
                        }
                    }
                }
            }
        });

        var compactResult = result["payload"]?["result"];
        Assert.False(compactResult?["projectionTruncated"]?.GetValue<bool>());
        Assert.Equal(30, compactResult?["projectionReturnedAnnotationCount"]?.GetValue<int>());
        Assert.False(compactResult?["annotationManagers"]?[0]?["projectionTruncated"]?.GetValue<bool>());
        Assert.True(result.ToJsonString().Length < 12_000);
    }
}
