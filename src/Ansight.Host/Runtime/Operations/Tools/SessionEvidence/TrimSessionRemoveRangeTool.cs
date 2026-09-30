using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal sealed class TrimSessionRemoveRangeTool : TrimSessionTool
{
    public TrimSessionRemoveRangeTool(OperationServices services)
        : base(services, SessionTimelineTrimMode.CutSelection, "removeSelectedRange")
    {
    }

    public override string Name => "ansight_trim_session_remove_range";

    protected override string Title => "Trim Session: Remove Range";

    protected override string Description => "Remove session data captured inside the selected timeline range. This mutates historical sessions and cannot be undone.";
}
