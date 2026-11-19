using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Point = System.Windows.Point;
using SystemColors = System.Windows.SystemColors;

namespace WinFlow.Views;

/// <summary>
/// Keeps the design tokens in Views/Theme.xaml in step with the app theme.
/// WPF has no native ThemeDictionaries, so — exactly like the tray menu —
/// the theme-aware brushes are overwritten in Application.Resources whenever
/// ApplicationThemeManager reports a change. All lookups are DynamicResource,
/// so every surface updates in place.
/// </summary>
internal static class WinFlowTheme
{
    private static bool _hooked;

    /// <summary>Call once at startup, before the first window is shown.</summary>
    public static void Initialize()
    {
        if (_hooked) return;
        _hooked = true;
        ApplicationThemeManager.Changed += (_, _) =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(Apply);
        Apply();
    }

    public static void Apply()
    {
        var res = System.Windows.Application.Current?.Resources;
        if (res == null) return;

        bool dark = ApplicationThemeManager.GetAppTheme() != ApplicationTheme.Light;
        bool highContrast = SystemParameters.HighContrast;

        void Set(string key, string hex) =>
            res[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

        if (highContrast)
        {
            // System colors only: the user's contrast palette must win everywhere.
            res["HeroBackgroundBrush"] = SystemColors.WindowBrush;
            res["HeroBorderBrush"] = SystemColors.WindowTextBrush;
            res["HeroTextBrush"] = SystemColors.WindowTextBrush;
            res["HeroSecondaryTextBrush"] = SystemColors.WindowTextBrush;
            res["KeycapBackgroundBrush"] = SystemColors.WindowBrush;
            res["KeycapBorderBrush"] = SystemColors.WindowTextBrush;
            res["KeycapTextBrush"] = SystemColors.WindowTextBrush;
            res["HeroIconBadgeBackgroundBrush"] = SystemColors.WindowBrush;
            res["SummaryCardBackgroundBrush"] = SystemColors.WindowBrush;
            res["SummaryCardBorderBrush"] = SystemColors.WindowTextBrush;
            return;
        }

        if (dark)
        {
            res["HeroBackgroundBrush"] = Gradient("#26303E", "#1B2029");
            Set("HeroBorderBrush", "#18FFFFFF");
            Set("HeroTextBrush", "#F5FFFFFF");
            Set("HeroSecondaryTextBrush", "#B3FFFFFF");
            Set("KeycapBackgroundBrush", "#1AFFFFFF");
            Set("KeycapBorderBrush", "#2AFFFFFF");
            Set("KeycapTextBrush", "#E6FFFFFF");
        }
        else
        {
            res["HeroBackgroundBrush"] = Gradient("#E9F0FA", "#F6F9FE");
            Set("HeroBorderBrush", "#14000000");
            Set("HeroTextBrush", "#E4000000");
            Set("HeroSecondaryTextBrush", "#9E000000");
            Set("KeycapBackgroundBrush", "#FFFFFF");
            Set("KeycapBorderBrush", "#24000000");
            Set("KeycapTextBrush", "#C8000000");
        }

        // Accent-derived tints track the current Windows accent.
        if (res["SystemAccentColorSecondary"] is Color accent)
        {
            res["HeroIconBadgeBackgroundBrush"] = Tint(accent, 0.22);
            res["SummaryCardBackgroundBrush"] = Tint(accent, 0.07);
            res["SummaryCardBorderBrush"] = Tint(accent, 0.22);
        }
    }

    private static LinearGradientBrush Gradient(string from, string to) => new(
        (Color)ColorConverter.ConvertFromString(from),
        (Color)ColorConverter.ConvertFromString(to),
        new Point(0, 0), new Point(1, 1));

    private static SolidColorBrush Tint(Color color, double opacity) =>
        new(color) { Opacity = opacity };
}