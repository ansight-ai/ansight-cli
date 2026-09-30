using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal sealed class TrimSessionKeepRangeTool : TrimSessionTool
{
    public TrimSessionKeepRangeTool(OperationServices services)
        : base(services, SessionTimelineTrimMode.KeepSelectionOnly, "keepOnlySelectedRange")
    {
    }

    public override string Name => "ansight_trim_session_keep_range";

    protected override string Title => "Trim Session: Keep Range";

    protected override string Description => "Keep only session data captured inside the selected timeline range and remove everything outside it. This mutates historical sessions and cannot be undone.";
}
