using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Ansight.Host.Audio;
using Ansight.Host.Audio.Ios;

namespace Ansight.Host.Tests.Audio;

public sealed class IosAudioNativeLibraryTests
{
    [Fact]
    public async Task NativeErrorsPreserveUtf8AndDoNotPoisonTheNextCall()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var error = await Assert.ThrowsAsync<AudioInjectionException>(() =>
            IosAudioHelper.RunAsync(["inspect", "--unknown-音", "value"], CancellationToken.None));
        Assert.Equal("invalid-arguments", error.Code);
        Assert.Contains("--unknown-音", error.Message);
        Assert.False(error.Diagnostics!["deliveryStarted"]!.GetValue<bool>());
        var response = await IosAudioHelper.RunAsync(["accessibility"], CancellationToken.None);
        Assert.True(response["success"]!.GetValue<bool>());
        Assert.True(response["accessibilityGranted"] is JsonValue value && value.TryGetValue<bool>(out _));
    }

    [Fact]
    public async Task CancellationIsIsolatedToItsNativeOperation()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var operation = IosAudioHelper.Create();
        try
        {
            IosAudioHelper.Cancel(operation);
            var response = IosAudioHelper.Execute(operation, "[\"accessibility\"]");
            try
            {
                var result = JsonNode.Parse(Marshal.PtrToStringUTF8(response)!)!;
                Assert.False(result["success"]!.GetValue<bool>());
                Assert.Equal("cancelled", result["code"]!.GetValue<string>());
                Assert.False(result["diagnostics"]!["deliveryStarted"]!.GetValue<bool>());
            }
            finally { IosAudioHelper.FreeString(response); }
            var independent = await IosAudioHelper.RunAsync(["accessibility"], CancellationToken.None);
            Assert.True(independent["success"]!.GetValue<bool>());
        }
        finally { IosAudioHelper.Destroy(operation); }
    }

    [Fact]
    public async Task AlreadyCancelledCallDoesNotEnterNativeCode()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            IosAudioHelper.RunAsync(["accessibility"], cancellation.Token));
    }

    [Fact]
    public async Task ConcurrentNativeCallsOwnTheirResponses()
    {
        if (!OperatingSystem.IsMacOS()) return;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(async index =>
        {
            var option = $"--invalid-{index}";
            var error = await Assert.ThrowsAsync<AudioInjectionException>(() =>
                IosAudioHelper.RunAsync(["inspect", option, "value"], CancellationToken.None));
            Assert.Contains(option + ".", error.Message);
            Assert.False(error.Diagnostics!["deliveryStarted"]!.GetValue<bool>());
        }));
    }
}
