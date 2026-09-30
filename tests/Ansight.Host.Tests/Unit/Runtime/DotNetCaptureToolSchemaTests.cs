using Ansight.Host.Runtime.DotNetProfiling;
using Ansight.Host.Runtime.Operations;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class DotNetCaptureToolSchemaTests
{
    [Fact]
    public void TryReadRequest_ParsesArtifactCaptureContract()
    {
        var applicationPath = Path.Combine(Path.GetTempPath(), "Example.apk");
        var symbolsPath = Path.Combine(Path.GetTempPath(), "symbols");
        var arguments = new JsonObject
        {
            ["applicationPath"] = applicationPath,
            ["appId"] = "com.example.app",
            ["deviceId"] = "emulator-5554",
            ["durationSeconds"] = 42,
            ["symbolsPath"] = symbolsPath
        };

        var success = DotNetCaptureToolSchemas.TryReadRequest(arguments, out var request, out var error);

        Assert.True(success, error);
        Assert.NotNull(request);
        Assert.Equal(applicationPath, request.ApplicationPath);
        Assert.Equal("com.example.app", request.AppId);
        Assert.Equal("emulator-5554", request.DeviceId);
        Assert.Equal(TimeSpan.FromSeconds(42), request.Duration);
        Assert.Equal(symbolsPath, request.SymbolsPath);
    }

    [Theory]
    [InlineData("applicationPath", "applicationPath is required.")]
    [InlineData("appId", "appId is required.")]
    [InlineData("deviceId", "deviceId is required.")]
    public void TryReadRequest_RequiresArtifactLaunchIdentity(string missingProperty, string expectedError)
    {
        var arguments = new JsonObject
        {
            ["applicationPath"] = "/tmp/Example.apk",
            ["appId"] = "com.example.app",
            ["deviceId"] = "emulator-5554"
        };
        arguments.Remove(missingProperty);

        var success = DotNetCaptureToolSchemas.TryReadRequest(arguments, out var request, out var error);

        Assert.False(success);
        Assert.Null(request);
        Assert.Equal(expectedError, error);
    }
}
