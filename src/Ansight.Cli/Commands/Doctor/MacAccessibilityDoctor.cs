using System.Text.Json.Nodes;
using Ansight.Host.Audio;
using Ansight.Host.Audio.Ios;

namespace Ansight.Cli.Commands.Doctor;

internal static class MacAccessibilityDoctor
{
    internal const string CheckName = "permission.accessibility";

    internal static async Task<DoctorCheck> CheckAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var response = await IosAudioHelper.RunAsync(["accessibility"], timeout.Token).ConfigureAwait(false);
            return FromResponse(response);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable("probe-timeout", "The Ansight process's Accessibility permission could not be checked within five seconds.");
        }
        catch (AudioInjectionException exception)
        {
            return Unavailable(exception.Code, exception.Message);
        }
    }

    internal static DoctorCheck FromResponse(JsonObject response)
    {
        if (response["accessibilityGranted"] is not JsonValue value || !value.TryGetValue<bool>(out var granted))
            return Unavailable("not-checked", "The Ansight process did not report its Accessibility permission. Reinstall the matching Ansight build.");

        return granted
            ? new DoctorCheck(CheckName, "granted", true, false,
                "The Ansight process has Accessibility access in this launch context. Run the host from the same launcher; Simulator audio-route selection is checked separately.", null)
            : Unavailable("not-granted", "The Ansight process lacks Accessibility access. " + DoctorCheckGuidance.GetInstallInstructions(CheckName));
    }

    private static DoctorCheck Unavailable(string status, string message) =>
        new(CheckName, status, false, false, message, null);
}
