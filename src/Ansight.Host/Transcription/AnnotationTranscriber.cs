using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Ansight.Host.Transcription;

// Uses Moonshine's native C API, also used by its Swift and Android bindings.
internal static class AnnotationTranscriber
{
    internal const int MaximumBytes = 4 * 1024 * 1024;
    internal const string Model = "Moonshine Tiny Streaming (English)";
    private const int SampleRate = 16000;
    private const int MaximumSeconds = 120;
    private const int EngineArchitecture = 2;
    private const int NativeAbiVersion = 30000;
    private static readonly SemaphoreSlim gate = new(1, 1);
    private static readonly HttpClient downloads = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static NativeApi? native;
    private static int activeHandle = -1;
    private static string? activeModelPath;
    private static Timer? idleTimer;
    private static long lastUseTimestamp;
    private static readonly TimeSpan modelIdleTimeout = TimeSpan.FromMinutes(2);
    private static readonly JsonDocument manifest = ReadManifest();

    private static string Version => manifest.RootElement.GetProperty("version").GetString()!;

    private static JsonDocument ReadManifest()
    {
        using var stream = typeof(AnnotationTranscriber).Assembly.GetManifestResourceStream("Ansight.Host.Transcription.moonshine-manifest.json")!;
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
        var hasInstalledModel = IsSupported
            && File.Exists(Path.Combine(root, "ready"))
            && File.Exists(GetLibraryPath(root))
            && manifest.RootElement.GetProperty("models").EnumerateArray()
                .All(asset => File.Exists(Path.Combine(root, "model", asset.GetProperty("name").GetString()!)));
        return hasInstalledModel;
    }

    internal static async Task PrepareAsync(string dataDirectory, CancellationToken cancellationToken)
    {
        if (!IsSupported)
        {
            throw new InvalidOperationException("Local dictation is not supported on this system yet. On Mac it currently requires Apple Silicon and macOS 15 or newer.");
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (IsInstalled(dataDirectory))
            {
                return;
            }

            var root = Root(dataDirectory);
            Directory.CreateDirectory(Path.Combine(root, "model"));
            if (!File.Exists(GetLibraryPath(root)) || !OperatingSystem.IsMacOS())
            {
                await InstallRuntimeAsync(root, cancellationToken);
            }

            foreach (var asset in manifest.RootElement.GetProperty("models").EnumerateArray())
            {
                await DownloadAsync(asset, Path.Combine(root, "model", asset.GetProperty("name").GetString()!), cancellationToken);
            }

            // Load and validate the model before marking the installation complete.
            var api = GetNative(root);
            var handle = api.load(Path.Combine(root, "model"), EngineArchitecture, IntPtr.Zero, 0, NativeAbiVersion);
            if (handle < 0)
            {
                throw new InvalidOperationException("The local speech model could not load. Retry the download.");
            }

            api.free(handle);
            await File.WriteAllTextAsync(Path.Combine(root, "ready"), Version, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task InstallRuntimeAsync(string root, CancellationToken cancellationToken)
    {
        var runtime = manifest.RootElement.GetProperty("runtimes").GetProperty(Platform);
        var wheel = Path.Combine(root, "runtime.whl");
        await DownloadAsync(runtime, wheel, cancellationToken);

        using (var archive = ZipFile.OpenRead(wheel))
        {
            foreach (var entry in archive.Entries)
            {
                var name = Path.GetFileName(entry.FullName);
                var isLibrary = name.EndsWith(".dylib", StringComparison.Ordinal)
                    || name.EndsWith(".dll", StringComparison.Ordinal)
                    || name.Contains(".so", StringComparison.Ordinal);
                var isNativeEntry = entry.FullName.StartsWith("moonshine_voice/", StringComparison.Ordinal) && isLibrary;

                if (isNativeEntry)
                {
                    entry.ExtractToFile(Path.Combine(root, name), true);
                }
                else if (entry.FullName.Contains(".dist-info/", StringComparison.Ordinal)
                    && (name.StartsWith("LICENSE", StringComparison.Ordinal) || name.StartsWith("NOTICE", StringComparison.Ordinal)))
                {
                    entry.ExtractToFile(Path.Combine(root, "Moonshine-" + name), true);
                }
            }
        }

        File.Delete(wheel);
    }

    private static string GetLibraryPath(string root)
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, LibraryName);
        return OperatingSystem.IsMacOS() && File.Exists(packaged) ? packaged : Path.Combine(root, LibraryName);
    }

    private static async Task DownloadAsync(JsonElement asset, string path, CancellationToken token)
    {
        var expected = asset.GetProperty("sha256").GetString()!;
        if (File.Exists(path) && await HashAsync(path, token) == expected)
        {
            return;
        }

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
                    if (total > expectedSize)
                    {
                        throw new InvalidDataException("Speech model download exceeded its expected size.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
                if (total != expectedSize)
                {
                    throw new InvalidDataException("Speech model download was incomplete. Please retry.");
                }
            }
            if (await HashAsync(temporary, token) != expected)
            {
                throw new InvalidDataException("Speech model download failed its integrity check. Please retry.");
            }

            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token)).ToLowerInvariant();
    }

    internal static float[] ReadAudio(byte[] audio, string contentType)
    {
        const int HeaderSize = 44;
        const int BytesPerSample = 2;
        var validPayload = contentType.Split(';')[0].Trim() == "audio/wav"
            && audio.Length >= HeaderSize + BytesPerSample
            && audio.Length <= MaximumBytes;
        if (!validPayload)
        {
            throw new InvalidDataException("Record a non-empty WAV comment of up to two minutes.");
        }

        var span = audio.AsSpan();
        var hasWavHeader = span[..4].SequenceEqual("RIFF"u8)
            && span.Slice(8, 4).SequenceEqual("WAVE"u8)
            && span.Slice(12, 4).SequenceEqual("fmt "u8)
            && BinaryPrimitives.ReadInt32LittleEndian(span[16..]) == 16
            && span.Slice(36, 4).SequenceEqual("data"u8);
        var hasRequiredFormat = BinaryPrimitives.ReadInt16LittleEndian(span[20..]) == 1
            && BinaryPrimitives.ReadInt16LittleEndian(span[22..]) == 1
            && BinaryPrimitives.ReadInt32LittleEndian(span[24..]) == SampleRate
            && BinaryPrimitives.ReadInt16LittleEndian(span[34..]) == 16;
        if (!hasWavHeader || !hasRequiredFormat)
        {
            throw new InvalidDataException("Dictation requires mono 16 kHz PCM16 WAV audio.");
        }

        var bytes = BinaryPrimitives.ReadInt32LittleEndian(span[40..]);
        var validLength = bytes > 0
            && bytes % BytesPerSample == 0
            && bytes == audio.Length - HeaderSize
            && bytes <= SampleRate * BytesPerSample * MaximumSeconds;
        if (!validLength)
        {
            throw new InvalidDataException("Record a non-empty comment of up to two minutes.");
        }

        var samples = new float[bytes / BytesPerSample];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(span[(HeaderSize + i * BytesPerSample)..]) / 32768f;
        }

        return samples;
    }

    internal static async Task<string> TranscribeAsync(byte[] audio, string contentType, string dataDirectory, CancellationToken cancellationToken)
    {
        var samples = ReadAudio(audio, contentType);
        if (!IsInstalled(dataDirectory))
        {
            throw new InvalidOperationException("Download the local speech model before dictating.");
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = Root(dataDirectory);
                var api = GetNative(root);
                var modelPath = Path.Combine(root, "model");
                if (activeHandle >= 0 && activeModelPath != modelPath)
                {
                    ReleaseModel();
                }

                if (activeHandle < 0)
                {
                    activeHandle = api.load(modelPath, EngineArchitecture, IntPtr.Zero, 0, NativeAbiVersion);
                    if (activeHandle < 0)
                    {
                        throw new InvalidOperationException("Unable to load the local speech model.");
                    }

                    activeModelPath = modelPath;
                }

                try
                {
                    var code = api.transcribe(activeHandle, samples, (ulong)samples.Length, SampleRate, 0, out var pointer);
                    cancellationToken.ThrowIfCancellationRequested();

                    if (code != 0 || pointer == IntPtr.Zero)
                    {
                        throw new InvalidOperationException("Local transcription failed. Try recording again.");
                    }

                    var transcript = Marshal.PtrToStructure<Transcript>(pointer);
                    var texts = new List<string>();
                    for (ulong i = 0; i < transcript.count; i++)
                    {
                        var line = Marshal.PtrToStructure<Line>(transcript.lines + checked((int)i * Marshal.SizeOf<Line>()));
                        var text = Marshal.PtrToStringUTF8(line.text)?.Trim();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            texts.Add(text);
                        }
                    }

                    if (texts.Count == 0)
                    {
                        throw new InvalidDataException("No speech was detected. Try recording again.");
                    }

                    return string.Join(" ", texts);
                }
                finally
                {
                    lastUseTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                    idleTimer ??= new Timer(static _ => ReleaseIdleModel());
                    idleTimer.Change(modelIdleTimeout, Timeout.InfiniteTimeSpan);
                }
            }, CancellationToken.None);
        }
        finally
        {
            gate.Release();
        }
    }

    private static void ReleaseIdleModel()
    {
        gate.Wait();
        try
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(lastUseTimestamp) >= modelIdleTimeout)
            {
                ReleaseModel();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static void ReleaseModel()
    {
        if (activeHandle >= 0)
        {
            native!.free(activeHandle);
            activeHandle = -1;
            activeModelPath = null;
        }
    }

    private static NativeApi GetNative(string root) => native ??= new NativeApi(GetLibraryPath(root));
    [StructLayout(LayoutKind.Sequential)]
    private struct Transcript
    {
        public IntPtr lines;
        public ulong count;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Line
    {
        public IntPtr text;
        public IntPtr audio;
        public nuint audioCount;
        public float start;
        public float duration;
        public ulong id;
        public byte complete;
        public byte updated;
        public byte isNew;
        public byte textChanged;
        public byte speakersChanged;
        public IntPtr speakerSpans;
        public ulong speakerCount;
        public uint latency;
        public IntPtr words;
        public ulong wordCount;
    }
    private sealed class NativeApi
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int LoadDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint architecture, IntPtr options, ulong count, int version);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void FreeDelegate(int handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int TranscribeDelegate(int handle, [In] float[] samples, ulong count, int rate, uint flags, out IntPtr transcript);
        internal readonly LoadDelegate load;
        internal readonly FreeDelegate free;
        internal readonly TranscribeDelegate transcribe;
        internal NativeApi(string path)
        {
            if (Marshal.SizeOf<Line>() != 88 || Marshal.SizeOf<Transcript>() != 16)
            {
                throw new InvalidOperationException("Unsupported Moonshine ABI.");
            }

            var library = NativeLibrary.Load(path);
            load = Marshal.GetDelegateForFunctionPointer<LoadDelegate>(NativeLibrary.GetExport(library, "moonshine_load_transcriber_from_files"));
            free = Marshal.GetDelegateForFunctionPointer<FreeDelegate>(NativeLibrary.GetExport(library, "moonshine_free_transcriber"));
            transcribe = Marshal.GetDelegateForFunctionPointer<TranscribeDelegate>(NativeLibrary.GetExport(library, "moonshine_transcribe_without_streaming"));
        }
    }
}
