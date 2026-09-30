namespace Ansight.Infrastructure.Theming.Themes;

public abstract class ThemeBase : ITheme
{
    private readonly Lazy<IReadOnlyDictionary<string, Color>> values;

    protected ThemeBase()
    {
        values = new Lazy<IReadOnlyDictionary<string, Color>>(() =>
            typeof(IColorSet)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .ToDictionary(
                    property => property.Name,
                    property => (Color)(property.GetValue(this) ?? throw new InvalidOperationException($"Theme property '{property.Name}' returned null.")),
                    StringComparer.Ordinal));
    }

    public abstract string Identifier { get; }

    public abstract string Name { get; }

    public abstract ThemeKind ThemeKind { get; }

    public abstract Color Transparent { get; }

    public abstract Color Accent { get; }

    public abstract Color AccentLight { get; }

    public abstract Color AccentMuted { get; }

    public abstract Color BgPrimary { get; }

    public abstract Color BgSecondary { get; }

    public abstract Color BgElevated { get; }

    public abstract Color BgCard { get; }

    public abstract Color BgCardHover { get; }

    public abstract Color TextPrimary { get; }

    public abstract Color BorderBrush { get; }

    public abstract Color BorderStrongBrush { get; }

    public abstract Color WorkspaceBackground { get; }

    public abstract Color SidebarBackground { get; }

    public abstract Color SidebarSurface { get; }

    public abstract Color SidebarBorder { get; }

    public abstract Color SidebarSelectionBackground { get; }

    public abstract Color SidebarSelectionBorder { get; }

    public abstract Color SidebarForeground { get; }

    public abstract Color SidebarMutedForeground { get; }

    public abstract Color SidebarSelectionForeground { get; }

    public abstract Color SidebarToggleBackground { get; }

    public abstract Color SidebarToggleBorder { get; }

    public abstract Color SidebarToggleForeground { get; }

    public abstract Color SidebarToggleSelectedBackground { get; }

    public abstract Color SidebarToggleSelectedForeground { get; }

    public abstract Color DialogScrimBackground { get; }

    public abstract Color PageBackground { get; }

    public abstract Color PanelBackground { get; }

    public abstract Color PageBackgroundContrast { get; }

    public abstract Color PanelBackgroundSubtle { get; }

    public abstract Color PanelBorder { get; }

    public abstract Color ActionBackground { get; }

    public abstract Color ActionBorder { get; }

    public abstract Color ActionInfoForeground { get; }

    public abstract Color ActionSuccessForeground { get; }

    public abstract Color ActionDangerForeground { get; }

    public abstract Color ActionDangerBorder { get; }

    public abstract Color SelectionBackground { get; }

    public abstract Color SelectionBorder { get; }

    public abstract Color TextEnabled { get; }

    public abstract Color TextSecondary { get; }

    public abstract Color TextMuted { get; }

    public abstract Color TextDisabled { get; }

    public abstract Color InverseTextEnabled { get; }

    public abstract Color InverseTextSecondary { get; }

    public abstract Color InverseTextMuted { get; }

    public abstract Color Divider { get; }

    public abstract Color InfoBackground { get; }

    public abstract Color InfoBorder { get; }

    public abstract Color InfoForeground { get; }

    public abstract Color InfoForegroundStrong { get; }

    public abstract Color Success { get; }

    public abstract Color SuccessLight { get; }

    public abstract Color SuccessBackground { get; }

    public abstract Color SuccessBorder { get; }

    public abstract Color SuccessForegroundStrong { get; }

    public abstract Color Warning { get; }

    public abstract Color WarningBackground { get; }

    public abstract Color WarningBorder { get; }

    public abstract Color WarningForegroundStrong { get; }

    public abstract Color Danger { get; }

    public abstract Color DangerBackground { get; }

    public abstract Color DangerBorder { get; }

    public abstract Color DangerForegroundStrong { get; }

    public abstract Color ChartBackground { get; }

    public abstract Color ChartGrid { get; }

    public abstract Color ChartAxis { get; }

    public abstract Color ChartLabel { get; }

    public abstract Color ChartProbe { get; }

    public abstract Color ChartTooltipBackground { get; }

    public abstract Color ChartTooltipBorder { get; }

    public abstract Color ChartPointOutline { get; }

    public abstract Color TrendsExcellent { get; }

    public abstract Color TrendsGood { get; }

    public abstract Color TrendsFair { get; }

    public abstract Color TrendsPoor { get; }

    public abstract Color TrendsCritical { get; }

    public abstract Color LogText { get; }

    public abstract Color LogRowEvenBackground { get; }

    public abstract Color LogRowOddBackground { get; }

    public abstract Color LogRowWarningBackground { get; }

    public abstract Color LogRowErrorBackground { get; }

    public abstract Color LogRowFatalBackground { get; }

    public abstract Color LogProbeFocusedBorder { get; }

    public abstract Color LogProbeRangeBorder { get; }

    public abstract Color LogProbeFocusedOverlay { get; }

    public abstract Color LogProbeRangeOverlay { get; }

    public IReadOnlyDictionary<string, Color> Values => values.Value;
}
