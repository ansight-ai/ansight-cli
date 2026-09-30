using System.Text.Json.Nodes;
using Ansight.Host.Audio;
using Ansight.Host.Audio.Ios;

namespace Ansight.Cli.Commands.Doctor;

internal static class BlackHoleDoctor
{
    internal const string CheckName = "device.ios.audio.blackhole";

    internal static async Task<DoctorCheck> CheckAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var response = await IosAudioHelper.RunAsync(["devices"], timeout.Token).ConfigureAwait(false);
            return FromDevices(response["devices"] as JsonArray ?? []);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable("probe-timeout", "CoreAudio registration could not be checked within five seconds.");
        }
        catch (AudioInjectionException exception)
        {
            return Unavailable(exception.Code, exception.Message);
        }
    }

    internal static DoctorCheck FromDevices(JsonArray devices)
    {
        var registered = devices.OfType<JsonObject>().FirstOrDefault(device =>
            device["uid"]?.GetValue<string>() == IosAudioHelper.DefaultDeviceUid
            && device["inputChannels"]?.GetValue<int>() > 0
            && device["outputChannels"]?.GetValue<int>() > 0);
        return registered is not null
            ? new DoctorCheck(CheckName, "registered", true, false,
                "BlackHole 2ch is registered with CoreAudio for input and output. Select it in Simulator > I/O > Audio Input before injecting audio; registration alone does not verify that route.",
                IosAudioHelper.DefaultDeviceUid)
            : Unavailable("not-registered", "BlackHole 2ch is not registered as a duplex CoreAudio device. " + DoctorCheckGuidance.GetInstallInstructions(CheckName));
    }

    private static DoctorCheck Unavailable(string status, string message) =>
        new(CheckName, status, false, false, message, null);
}
