using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ansight.Host.SimulatorAgent.OpenAi;

// Uses Moonshine's native C API, also used by its Swift and Android bindings.
internal static class AnnotationTranscriber
{
    internal const int MaximumBytes = 4 * 1024 * 1024;
    internal const string Model = "Moonshine Tiny Streaming (English)";
    private const string Version = "0.1.6-tiny-streaming-en-26_11_02";
    private static readonly SemaphoreSlim gate = new(1, 1);
    private static readonly HttpClient downloads = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static NativeApi? native;
    private static readonly JsonDocument manifest = ReadManifest();

    private static JsonDocument ReadManifest()
    {
        using var stream = typeof(AnnotationTranscriber).Assembly.GetManifestResourceStream("Ansight.Host.SimulatorAgent.OpenAi.moonshine-manifest.json")!;
        return JsonDocument.Parse(stream);
    }

    private static string Platform => $"{(OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsWindows() ? "win" : "linux")}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
    private static string Root(string dataDirectory) => Path.Combine(dataDirectory, "speech", "moonshine", Version, Platform);
    private static string LibraryName => OperatingSystem.IsMacOS() ? "libmoonshine.dylib" : OperatingSystem.IsWindows() ? "moonshine.dll" : "libmoonshine.so";
    internal static bool IsSupported => manifest.RootElement.GetProperty("runtimes").TryGetProperty(Platform, out _)
        && (!OperatingSystem.IsMacOS() || OperatingSystem.IsMacOSVersionAtLeast(15));
    internal static bool IsInstalled(string dataDirectory)
    {
        var root = Root(dataDirectory);
        return IsSupported && File.Exists(Path.Combine(root, "ready")) && File.Exists(Path.Combine(root, LibraryName))
            && manifest.RootElement.GetProperty("models").EnumerateArray().All(asset => File.Exists(Path.Combine(root, "model", asset.GetProperty("name").GetString()!)));
    }

    internal static async Task PrepareAsync(string dataDirectory, CancellationToken cancellationToken)
    {
        if (!IsSupported) throw new InvalidOperationException("Local dictation is not supported on this system yet. On Mac it currently requires Apple Silicon and macOS 15 or newer.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (IsInstalled(dataDirectory)) return;
            var root = Root(dataDirectory);
            Directory.CreateDirectory(Path.Combine(root, "model"));
            var runtime = manifest.RootElement.GetProperty("runtimes").GetProperty(Platform);
            var wheel = Path.Combine(root, "runtime.whl");
            await DownloadAsync(runtime, wheel, cancellationToken);
            using (var archive = ZipFile.OpenRead(wheel))
            {
                foreach (var entry in archive.Entries)
                {
                    var name = Path.GetFileName(entry.FullName);
                    if (entry.FullName.StartsWith("moonshine_voice/", StringComparison.Ordinal)
                        && (name.EndsWith(".dylib") || name.EndsWith(".dll") || name.Contains(".so")))
                    {
                        entry.ExtractToFile(Path.Combine(root, name), true);
                    }
                    else if (entry.FullName.Contains(".dist-info/") && (name.StartsWith("LICENSE") || name.StartsWith("NOTICE")))
                    {
                        entry.ExtractToFile(Path.Combine(root, "Moonshine-" + name), true);
                    }
                }
            }
            foreach (var asset in manifest.RootElement.GetProperty("models").EnumerateArray())
            {
                await DownloadAsync(asset, Path.Combine(root, "model", asset.GetProperty("name").GetString()!), cancellationToken);
            }
            File.Delete(wheel);
            // Load and validate the model before marking the installation complete.
            var api = GetNative(root);
            var handle = api.Load(Path.Combine(root, "model"), 2, IntPtr.Zero, 0, 30000);
            if (handle < 0) throw new InvalidOperationException("The local speech model could not load. Retry the download.");
            api.Free(handle);
            await File.WriteAllTextAsync(Path.Combine(root, "ready"), Version, cancellationToken);
        }
        finally { gate.Release(); }
    }

    private static async Task DownloadAsync(JsonElement asset, string path, CancellationToken token)
    {
        var expected = asset.GetProperty("sha256").GetString()!;
        if (File.Exists(path) && await HashAsync(path, token) == expected) return;
        var temporary = path + ".part";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, asset.GetProperty("url").GetString());
            request.Headers.UserAgent.ParseAdd("Ansight-Local-Dictation/1.0");
            using var response = await downloads.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = File.Create(temporary))
            {
                var buffer = new byte[65536];
                var expectedSize = asset.GetProperty("size").GetInt64();
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, token)) != 0)
                {
                    total += count;
                    if (total > expectedSize) throw new InvalidDataException("Speech model download exceeded its expected size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
                if (total != expectedSize) throw new InvalidDataException("Speech model download was incomplete. Please retry.");
            }
            if (await HashAsync(temporary, token) != expected) throw new InvalidDataException("Speech model download failed its integrity check. Please retry.");
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token)).ToLowerInvariant();
    }

    internal static float[] ReadAudio(byte[] audio, string contentType)
    {
        if (contentType.Split(';')[0].Trim() != "audio/wav" || audio.Length < 46 || audio.Length > MaximumBytes)
            throw new InvalidDataException("Record a non-empty WAV comment of up to two minutes.");
        var span = audio.AsSpan();
        if (!span[..4].SequenceEqual("RIFF"u8) || !span.Slice(8, 4).SequenceEqual("WAVE"u8)
            || !span.Slice(12, 4).SequenceEqual("fmt "u8) || BinaryPrimitives.ReadInt32LittleEndian(span[16..]) != 16
            || BinaryPrimitives.ReadInt16LittleEndian(span[20..]) != 1 || BinaryPrimitives.ReadInt16LittleEndian(span[22..]) != 1
            || BinaryPrimitives.ReadInt32LittleEndian(span[24..]) != 16000 || BinaryPrimitives.ReadInt16LittleEndian(span[34..]) != 16
            || !span.Slice(36, 4).SequenceEqual("data"u8))
            throw new InvalidDataException("Dictation requires mono 16 kHz PCM16 WAV audio.");
        var bytes = BinaryPrimitives.ReadInt32LittleEndian(span[40..]);
        if (bytes <= 0 || bytes % 2 != 0 || bytes != audio.Length - 44 || bytes > 16000 * 2 * 120)
            throw new InvalidDataException("Record a non-empty comment of up to two minutes.");
        var samples = new float[bytes / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(span[(44 + i * 2)..]) / 32768f;
        return samples;
    }

    internal static async Task<string> TranscribeAsync(byte[] audio, string contentType, string dataDirectory, CancellationToken cancellationToken)
    {
        var samples = ReadAudio(audio, contentType);
        if (!IsInstalled(dataDirectory)) throw new InvalidOperationException("Download the local speech model before dictating.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = Root(dataDirectory);
                var api = GetNative(root);
                var handle = api.Load(Path.Combine(root, "model"), 2, IntPtr.Zero, 0, 30000);
                if (handle < 0) throw new InvalidOperationException("Unable to load the local speech model.");
                try
                {
                    var code = api.Transcribe(handle, samples, (ulong)samples.Length, 16000, 0, out var pointer);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (code != 0 || pointer == IntPtr.Zero) throw new InvalidOperationException("Local transcription failed. Try recording again.");
                    var transcript = Marshal.PtrToStructure<Transcript>(pointer);
                    var texts = new List<string>();
                    for (ulong i = 0; i < transcript.Count; i++)
                    {
                        var line = Marshal.PtrToStructure<Line>(transcript.Lines + checked((int)i * Marshal.SizeOf<Line>()));
                        var text = Marshal.PtrToStringUTF8(line.Text)?.Trim();
                        if (!string.IsNullOrWhiteSpace(text)) texts.Add(text);
                    }
                    if (texts.Count == 0) throw new InvalidDataException("No speech was detected. Try recording again.");
                    return string.Join(" ", texts);
                }
                finally { api.Free(handle); } // Unload model memory between comments, including failures/cancellation.
            }, CancellationToken.None);
        }
        finally { gate.Release(); }
    }

    private static NativeApi GetNative(string root) => native ??= new NativeApi(Path.Combine(root, LibraryName));
    [StructLayout(LayoutKind.Sequential)] private struct Transcript { public IntPtr Lines; public ulong Count; }
    [StructLayout(LayoutKind.Sequential)] private struct Line
    {
        public IntPtr Text, Audio;
        public nuint AudioCount;
        public float Start, Duration;
        public ulong Id;
        public byte Complete, Updated, New, TextChanged, SpeakersChanged;
        public IntPtr SpeakerSpans;
        public ulong SpeakerCount;
        public uint Latency;
        public IntPtr Words;
        public ulong WordCount;
    }
    private sealed class NativeApi
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int LoadDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint architecture, IntPtr options, ulong count, int version);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void FreeDelegate(int handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int TranscribeDelegate(int handle, [In] float[] samples, ulong count, int rate, uint flags, out IntPtr transcript);
        internal readonly LoadDelegate Load;
        internal readonly FreeDelegate Free;
        internal readonly TranscribeDelegate Transcribe;
        internal NativeApi(string path)
        {
            if (Marshal.SizeOf<Line>() != 88 || Marshal.SizeOf<Transcript>() != 16) throw new InvalidOperationException("Unsupported Moonshine ABI.");
            var library = NativeLibrary.Load(path);
            Load = Marshal.GetDelegateForFunctionPointer<LoadDelegate>(NativeLibrary.GetExport(library, "moonshine_load_transcriber_from_files"));
            Free = Marshal.GetDelegateForFunctionPointer<FreeDelegate>(NativeLibrary.GetExport(library, "moonshine_free_transcriber"));
            Transcribe = Marshal.GetDelegateForFunctionPointer<TranscribeDelegate>(NativeLibrary.GetExport(library, "moonshine_transcribe_without_streaming"));
        }
    }
}
