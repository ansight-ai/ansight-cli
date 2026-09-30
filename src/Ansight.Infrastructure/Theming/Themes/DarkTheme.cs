namespace Ansight.Infrastructure.Theming.Themes;

internal sealed class DarkTheme : ThemeBase
{
    public override string Identifier => ThemeIdentifiers.Dark;

    public override string Name => "Dark";

    public override ThemeKind ThemeKind => ThemeKind.Dark;

    public override Color Transparent => new(0, 0, 0, 0);

    public override Color Accent => Color.FromHex("#FA441F");

    public override Color AccentLight => Color.FromHex("#FC8B70");

    public override Color AccentMuted => Color.FromHex("#33FA441F");

    public override Color BgPrimary => Color.FromHex("#0F0F12");

    public override Color BgSecondary => Color.FromHex("#1A1A1E");

    public override Color BgElevated => Color.FromHex("#252529");

    public override Color BgCard => Color.FromHex("#0AFFFFFF");

    public override Color BgCardHover => Color.FromHex("#12FFFFFF");

    public override Color TextPrimary => Color.FromHex("#F6F2EB");

    public override Color TextSecondary => Color.FromHex("#C0B8AF");

    public override Color TextMuted => Color.FromHex("#8F8881");

    public override Color BorderBrush => Color.FromHex("#14FFFFFF");

    public override Color BorderStrongBrush => Color.FromHex("#26FFFFFF");

    public override Color WorkspaceBackground => BgPrimary;

    public override Color SidebarBackground => BgPrimary;

    public override Color SidebarSurface => BgCard;

    public override Color SidebarBorder => BorderBrush;

    public override Color SidebarSelectionBackground => Accent;

    public override Color SidebarSelectionBorder => Accent;

    public override Color SidebarForeground => TextPrimary;

    public override Color SidebarMutedForeground => TextMuted;

    public override Color SidebarSelectionForeground => InverseTextEnabled;

    public override Color SidebarToggleBackground => BgCard;

    public override Color SidebarToggleBorder => BorderBrush;

    public override Color SidebarToggleForeground => TextMuted;

    public override Color SidebarToggleSelectedBackground => BgElevated;

    public override Color SidebarToggleSelectedForeground => TextPrimary;

    public override Color DialogScrimBackground => Color.FromHex("#66000000");

    public override Color PageBackground => BgPrimary;

    public override Color PanelBackground => BgElevated;

    public override Color PageBackgroundContrast => BgElevated;

    public override Color PanelBackgroundSubtle => BgCard;

    public override Color PanelBorder => BorderBrush;

    public override Color ActionBackground => BgCard;

    public override Color ActionBorder => BorderStrongBrush;

    public override Color ActionInfoForeground => TextPrimary;

    public override Color ActionSuccessForeground => SuccessLight;

    public override Color ActionDangerForeground => Danger;

    public override Color ActionDangerBorder => DangerBorder;

    public override Color SelectionBackground => AccentMuted;

    public override Color SelectionBorder => AccentLight;

    public override Color TextEnabled => TextPrimary;

    public override Color TextDisabled => Color.FromHex("#52525B");

    public override Color InverseTextEnabled => Color.FromHex("#FFFFFF");

    public override Color InverseTextSecondary => Color.FromHex("#F4F4F5");

    public override Color InverseTextMuted => Color.FromHex("#D4D4D8");

    public override Color Divider => BorderBrush;

    public override Color InfoBackground => BgCard;

    public override Color InfoBorder => BorderBrush;

    public override Color InfoForeground => TextSecondary;

    public override Color InfoForegroundStrong => TextPrimary;

    public override Color Success => Color.FromHex("#22C55E");

    public override Color SuccessLight => Color.FromHex("#4ADE80");

    public override Color SuccessBackground => Color.FromHex("#1F22C55E");

    public override Color SuccessBorder => Color.FromHex("#4D22C55E");

    public override Color SuccessForegroundStrong => SuccessLight;

    public override Color Warning => Color.FromHex("#EAB308");

    public override Color WarningBackground => Color.FromHex("#1FEAB308");

    public override Color WarningBorder => Color.FromHex("#4DEAB308");

    public override Color WarningForegroundStrong => Warning;

    public override Color Danger => Color.FromHex("#EF4444");

    public override Color DangerBackground => Color.FromHex("#1FEF4444");

    public override Color DangerBorder => Color.FromHex("#4DEF4444");

    public override Color DangerForegroundStrong => Danger;

    public override Color ChartBackground => BgCard;

    public override Color ChartGrid => BorderBrush;

    public override Color ChartAxis => TextMuted;

    public override Color ChartLabel => TextSecondary;

    public override Color ChartProbe => Color.FromHex("#8CFAFAFA");

    public override Color ChartTooltipBackground => BgElevated;

    public override Color ChartTooltipBorder => BorderStrongBrush;

    public override Color ChartPointOutline => BgPrimary;

    public override Color TrendsExcellent => Success;

    public override Color TrendsGood => SuccessLight;

    public override Color TrendsFair => Warning;

    public override Color TrendsPoor => Accent;

    public override Color TrendsCritical => Danger;

    public override Color LogText => TextPrimary;

    public override Color LogRowEvenBackground => BgCard;

    public override Color LogRowOddBackground => BgSecondary;

    public override Color LogRowWarningBackground => WarningBackground;

    public override Color LogRowErrorBackground => DangerBackground;

    public override Color LogRowFatalBackground => Color.FromHex("#33EF4444");

    public override Color LogProbeFocusedBorder => Accent;

    public override Color LogProbeRangeBorder => AccentLight;

    public override Color LogProbeFocusedOverlay => AccentMuted;

    public override Color LogProbeRangeOverlay => Color.FromHex("#1AFC8B70");
}
