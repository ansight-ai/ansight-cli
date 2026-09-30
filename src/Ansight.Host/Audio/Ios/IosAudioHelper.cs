using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Host.Audio.Ios;

internal static class IosAudioHelper
{
    private const string Library = "AnsightAudioInjection";
    internal const string DefaultDeviceUid = "BlackHole2ch_UID";
    internal const string DeviceUidEnvironmentVariable = "ANSIGHT_AUDIO_DEVICE_UID";

    internal static Task<JsonObject> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Snapshot caller-owned arguments before dispatching the blocking native operation.
        var argumentsJson = JsonSerializer.Serialize(arguments);
        return Task.Run(() => Run(argumentsJson, cancellationToken), cancellationToken);
    }

    private static JsonObject Run(string argumentsJson, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = Create();
            try
            {
                // Disposing the registration waits for an in-flight callback before Destroy.
                // Always wait for Execute to unwind its audio resources, even when cancelled.
                using var registration = cancellationToken.Register(() => Cancel(operation));
                var response = Execute(operation, argumentsJson);
                string? output;
                try { output = Marshal.PtrToStringUTF8(response); }
                finally { if (response != IntPtr.Zero) FreeString(response); }
                cancellationToken.ThrowIfCancellationRequested();
                JsonObject result;
                try
                {
                    result = output is null ? throw new JsonException("Missing native response.")
                        : JsonNode.Parse(output) as JsonObject ?? throw new JsonException("Expected a JSON object.");
                }
                catch (JsonException)
                {
                    throw new AudioInjectionException("audio-helper-invalid-response", "The macOS audio library returned invalid output.");
                }
                if (result["success"]?.GetValue<bool>() != true)
                    throw new AudioInjectionException(result["code"]?.GetValue<string>() ?? "audio-helper-error",
                        result["message"]?.GetValue<string>() ?? "The macOS audio library failed.",
                        result["diagnostics"] as JsonObject);
                return result;
            }
            finally { Destroy(operation); }
        }
        catch (DllNotFoundException)
        {
            // Keep the existing diagnostic codes stable for CLI clients.
            throw new AudioInjectionException("audio-helper-missing", "The bundled macOS audio library could not be loaded. Reinstall the Ansight build for this Mac.", new() { ["deliveryStarted"] = false });
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or BadImageFormatException)
        {
            throw new AudioInjectionException("audio-helper-unavailable", $"The bundled macOS audio library is incompatible: {exception.Message}", new() { ["deliveryStarted"] = false });
        }
    }

    [DllImport(Library, EntryPoint = "AnsightAudioInjectionCreate", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr Create();

    [DllImport(Library, EntryPoint = "AnsightAudioInjectionExecute", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr Execute(IntPtr operation, [MarshalAs(UnmanagedType.LPUTF8Str)] string argumentsJson);

    [DllImport(Library, EntryPoint = "AnsightAudioInjectionCancel", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Cancel(IntPtr operation);

    [DllImport(Library, EntryPoint = "AnsightAudioInjectionDestroy", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Destroy(IntPtr operation);

    [DllImport(Library, EntryPoint = "AnsightAudioInjectionFreeString", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FreeString(IntPtr value);
}
