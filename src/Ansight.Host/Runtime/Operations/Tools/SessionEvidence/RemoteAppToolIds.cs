namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal static class RemoteAppToolIds
{
    public const string UiGetScreenshot = "ui.get_screenshot";
    public const string UiGetVisualTree = VisualTreeContract.NativeToolId;
    public const string MauiGetVisualTree = VisualTreeContract.MauiToolId;
    public const string ReactGetComponentTree = VisualTreeContract.ReactComponentToolId;
    public const string ReactGetShadowTree = VisualTreeContract.ReactShadowToolId;
    public const string FlutterGetWidgetTree = VisualTreeContract.FlutterToolId;
    public const string DomGetDocument = VisualTreeContract.DomToolId;
}
