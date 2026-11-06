using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Ansight.RemoteSimulator.Core.Access;

namespace Ansight.RemoteSimulator.Core.Server;

public sealed record LocalCompanionRequest(string Method, string Path, JsonElement? Body);
