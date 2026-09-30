namespace Ansight.Host.Tests.Unit.Audio;

internal static class AndroidAudioFlingerFixtures
{
    // Redacted excerpt of the 2026-09-08 live emulator dump. Intermediate 50 ms
    // rows are omitted; real timestamps, continuation spacing and resolutions remain.
    internal const string UnhealthyContinuation = """
        Now:         09-08 18:10:37.007
        Input thread microphone, name AudioIn_36, type 3 (RECORD):
          Standby: no
          Sample rate: 16000 Hz
          Input device: 0x80000004 (AUDIO_DEVICE_IN_BUILTIN_MIC)
          Last read occurred (msecs): 15
          Frames read: 415615
          Hal stream dump:
              Signal power history (resolution: 1000.0 ms):
               09-08 18:10:11.031: [   -3.0   -3.0   -3.0   -3.0   -3.0   -3.0   -3.0   -3.0   -3.0   -3.0
               09-08 18:10:21.031:     -3.0   -3.0   -3.0   -3.0   -3.0   -3.0   -3.0   -3.0   -3.0   -3.0
               09-08 18:10:31.031:     -3.0   -3.0   -3.0   -3.0   -3.0
              Signal power history (resolution: 50.0 ms):
               09-08 18:10:35.481:     -3.0   -3.0   -3.0   -3.0   -3.0   -3.0   -3.0   -3.0   -3.0   -3.0
               09-08 18:10:35.981:     -3.0   -3.0   -3.0   -3.0   -3.0   -3.0   -3.0   -3.0   -3.0   -3.0
               09-08 18:10:36.481:     -3.0   -3.0   -3.0   -3.0   -3.0   -3.0   -3.0   -3.0   -3.0   -3.0

             -3.0 -|********************************

          1 Tracks of which 1 are active
          Local log:
           09-08 18:10:11.000 AT::add (redacted)
        Historical Thread Log 09-08 18:08:53.488 -
        - Input thread historical, name AudioIn_2E, type 3 (RECORD):
        -   Standby: yes
        -   Signal power history (resolution: 50.0 ms):
        -    09-08 18:08:52.944:    -77.7  -77.7  -77.2  -77.5  -77.5  -77.4  -77.2  -77.6  -77.5  -77.5
        """;

    internal static string QuietContinuation => UnhealthyContinuation.Replace("-3.0", "-77.5", StringComparison.Ordinal);
}
