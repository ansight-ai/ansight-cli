namespace Ansight.RemoteSimulator.Core.Simulator.Android.Input;

internal sealed record TextInputOperation(
    TextInputOperationKind Kind,
    string Value);
