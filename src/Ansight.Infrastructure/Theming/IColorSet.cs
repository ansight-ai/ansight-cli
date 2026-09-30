namespace Ansight.Infrastructure.Theming;

public interface IColorSet
{
    Color Transparent { get; }

    Color Accent { get; }

    Color AccentLight { get; }

    Color AccentMuted { get; }

    Color BgPrimary { get; }

    Color BgSecondary { get; }

    Color BgElevated { get; }

    Color BgCard { get; }

    Color BgCardHover { get; }

    Color TextPrimary { get; }

    Color BorderBrush { get; }

    Color BorderStrongBrush { get; }

    Color WorkspaceBackground { get; }

    Color SidebarBackground { get; }

    Color SidebarSurface { get; }

    Color SidebarBorder { get; }

    Color SidebarSelectionBackground { get; }

    Color SidebarSelectionBorder { get; }

    Color SidebarForeground { get; }

    Color SidebarMutedForeground { get; }

    Color SidebarSelectionForeground { get; }

    Color SidebarToggleBackground { get; }

    Color SidebarToggleBorder { get; }

    Color SidebarToggleForeground { get; }

    Color SidebarToggleSelectedBackground { get; }

    Color SidebarToggleSelectedForeground { get; }

    Color DialogScrimBackground { get; }

    Color PageBackground { get; }

    Color PanelBackground { get; }

    Color PageBackgroundContrast { get; }

    Color PanelBackgroundSubtle { get; }

    Color PanelBorder { get; }

    Color ActionBackground { get; }

    Color ActionBorder { get; }

    Color ActionInfoForeground { get; }

    Color ActionSuccessForeground { get; }

    Color ActionDangerForeground { get; }

    Color ActionDangerBorder { get; }

    Color SelectionBackground { get; }

    Color SelectionBorder { get; }

    Color TextEnabled { get; }

    Color TextSecondary { get; }

    Color TextMuted { get; }

    Color TextDisabled { get; }

    Color InverseTextEnabled { get; }

    Color InverseTextSecondary { get; }

    Color InverseTextMuted { get; }

    Color Divider { get; }

    Color InfoBackground { get; }

    Color InfoBorder { get; }

    Color InfoForeground { get; }

    Color InfoForegroundStrong { get; }

    Color Success { get; }

    Color SuccessLight { get; }

    Color SuccessBackground { get; }

    Color SuccessBorder { get; }

    Color SuccessForegroundStrong { get; }

    Color Warning { get; }

    Color WarningBackground { get; }

    Color WarningBorder { get; }

    Color WarningForegroundStrong { get; }

    Color Danger { get; }

    Color DangerBackground { get; }

    Color DangerBorder { get; }

    Color DangerForegroundStrong { get; }

    Color ChartBackground { get; }

    Color ChartGrid { get; }

    Color ChartAxis { get; }

    Color ChartLabel { get; }

    Color ChartProbe { get; }

    Color ChartTooltipBackground { get; }

    Color ChartTooltipBorder { get; }

    Color ChartPointOutline { get; }

    Color TrendsExcellent { get; }

    Color TrendsGood { get; }

    Color TrendsFair { get; }

    Color TrendsPoor { get; }

    Color TrendsCritical { get; }

    Color LogText { get; }

    Color LogRowEvenBackground { get; }

    Color LogRowOddBackground { get; }

    Color LogRowWarningBackground { get; }

    Color LogRowErrorBackground { get; }

    Color LogRowFatalBackground { get; }

    Color LogProbeFocusedBorder { get; }

    Color LogProbeRangeBorder { get; }

    Color LogProbeFocusedOverlay { get; }

    Color LogProbeRangeOverlay { get; }
}
