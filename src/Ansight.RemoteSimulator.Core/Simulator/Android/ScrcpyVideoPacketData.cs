using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Ansight.Adb;

namespace Ansight.RemoteSimulator.Core.Simulator.Android;

internal sealed record ScrcpyVideoPacketData(
    byte[] Data,
    bool IsConfiguration,
    bool IsKeyFrame,
    long PresentationTimestampMicroseconds);
