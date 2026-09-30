using System.Text.Json.Nodes;

namespace Ansight.Cli.Tests.Commands.Doctor;

public sealed class MacAccessibilityDoctorTests
{
    [Fact]
    public void DeniedPermissionIsOptionalAndCanBlockAnAudioPreflight()
    {
        var check = MacAccessibilityDoctor.FromResponse(new JsonObject { ["accessibilityGranted"] = false });
        Assert.False(check.IsSuccess);
        Assert.Equal("amber", check.Signal);
        Assert.Contains("Privacy_Accessibility", check.InstallInstructions);
        Assert.Contains("same launcher", check.InstallInstructions);
        var checks = new List<DoctorCheck> { check };
        DoctorCommand.RequireChecks(checks, [MacAccessibilityDoctor.CheckName]);
        Assert.True(checks[0].IsRequired);
        Assert.Equal("red", checks[0].Signal);
    }

    [Fact]
    public void GrantedPermissionDoesNotClaimAudioRouteReadiness()
    {
        var check = MacAccessibilityDoctor.FromResponse(new JsonObject { ["accessibilityGranted"] = true });
        Assert.True(check.IsSuccess);
        Assert.Equal("granted", check.Status);
        Assert.Contains("checked separately", check.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"accessibilityGranted\":\"true\"}")]
    public void MissingOrMalformedPermissionCannotPass(string json)
    {
        var check = MacAccessibilityDoctor.FromResponse(JsonNode.Parse(json)!.AsObject());
        Assert.False(check.IsSuccess);
        Assert.Equal("not-checked", check.Status);
    }
}
