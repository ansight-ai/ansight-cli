using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ansight.RemoteSimulator.Core.Annotations;
using Ansight.RemoteSimulator.Core.Devices;
using Ansight.RemoteSimulator.Core.Runtime;

namespace Ansight.RemoteSimulator.Core.Server;

internal sealed record RemoteStateResponse(
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<RemoteRuntimeDevice> Devices,
    IReadOnlyList<RemoteBootableDevice> BootableDevices,
    IReadOnlyDictionary<string, IReadOnlyList<RemoteInstalledApplication>> InstalledApplications,
    IReadOnlyList<RemoteAnnotationSession> AnnotationSessions,
    string? TrackerError,
    string InputBackend,
    string InputStatus,
    string VideoBackend,
    string VideoStatus);
