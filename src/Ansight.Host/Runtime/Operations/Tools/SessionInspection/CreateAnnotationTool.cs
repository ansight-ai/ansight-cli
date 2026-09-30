namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal sealed class CreateAnnotationTool : AnnotationMutationTool
{
    public CreateAnnotationTool(OperationServices services) : base(services, SessionAnnotationMutationKind.Create)
    {
    }
}
