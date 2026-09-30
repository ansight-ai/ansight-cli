namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal sealed class RemoveAnnotationTool : AnnotationMutationTool
{
    public RemoveAnnotationTool(OperationServices services) : base(services, SessionAnnotationMutationKind.Remove)
    {
    }
}
