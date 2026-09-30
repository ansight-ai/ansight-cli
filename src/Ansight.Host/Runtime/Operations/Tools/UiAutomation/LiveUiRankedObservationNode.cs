using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Operations.Tools.UiAutomation;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed record LiveUiRankedObservationNode(LiveUiNodeMatch Match, int Index);
