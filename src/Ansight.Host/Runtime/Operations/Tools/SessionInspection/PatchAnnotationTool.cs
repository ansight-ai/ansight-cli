namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal sealed class PatchAnnotationTool : AnnotationMutationTool
{
    public PatchAnnotationTool(OperationServices services) : base(services, SessionAnnotationMutationKind.Patch)
    {
    }
}
