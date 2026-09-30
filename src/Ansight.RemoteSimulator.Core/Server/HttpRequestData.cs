using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ansight.RemoteSimulator.Core.Server;

internal sealed record HttpRequestData(
    string Method,
    string Target,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body);
