namespace Ansight.Host.Runtime.State;

internal interface ISessionAnnotationMutator
{
    SessionAnnotationMutationResult MutateSessionAnnotation(string sessionId, SessionAnnotationMutation mutation);
}
