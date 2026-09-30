namespace Ansight.Host.Runtime.State;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using System.Text;
using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Pairing.Models;
using Ansight.Infrastructure.Logging;

internal readonly record struct MetricDeduplicationKey(byte ChannelId, long Value, DateTimeOffset CapturedAtUtc);
