using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace MiniProToolkit.App;

/// <summary>
/// Presentation-only lookup of the Obsidian theme brushes for code-behind chart drawing and inline
/// feedback text, so the hard-coded colours that used to assume a white page stay readable on the dark
/// canvas. It only reads resources: no hardware, configuration, opacity, numeric or command logic is
/// touched. When the key is missing (designer host, markup-only tests, a window that never merged the
/// dictionary) the previous literal colour is used, so drawing keeps working exactly as before.
/// </summary>
internal static class ObsidianTheme
{
    public static Brush BrushOf(FrameworkElement scope, string key, Color fallback)
    {
        if (scope.TryFindResource(key) is SolidColorBrush themeBrush)
        {
            if (themeBrush.IsFrozen)
                return themeBrush;
            var copy = themeBrush.Clone();
            copy.Freeze();
            return copy;
        }

        var fallbackBrush = new SolidColorBrush(fallback);
        fallbackBrush.Freeze();
        return fallbackBrush;
    }

    /// <summary>Same-value fallback for the theme palette, so call sites only name the key.</summary>
    public static Brush BrushOf(FrameworkElement scope, string key) => BrushOf(scope, key, Fallback(key));

    private static Color Fallback(string key) => key switch
    {
        "ObsidianChartBackgroundBrush" => Color.FromRgb(0x0D, 0x16, 0x22),
        "ObsidianChartGuideBrush" => Color.FromRgb(0x1B, 0x26, 0x34),
        "ObsidianTextPrimaryBrush" => Color.FromRgb(0xE5, 0xED, 0xF7),
        "ObsidianTextSecondaryBrush" => Color.FromRgb(0x98, 0xAB, 0xC0),
        "ObsidianAccentBrush" => Color.FromRgb(0x66, 0xDE, 0xC4),
        "ObsidianWarningBrush" => Color.FromRgb(0xED, 0xBA, 0x68),
        "ObsidianDangerBrush" => Color.FromRgb(0xF0, 0x8A, 0x7C),
        "ObsidianChartTemperatureBrush" => Color.FromRgb(0x66, 0xDE, 0xC4),
        "ObsidianChartRpmBrush" => Color.FromRgb(0x7F, 0xA7, 0xFF),
        "ObsidianChartTargetRpmBrush" => Color.FromRgb(0x5E, 0x77, 0x91),
        "ObsidianChartDutyBrush" => Color.FromRgb(0xC7, 0x92, 0xEA),
        "ObsidianChartPowerBrush" => Color.FromRgb(0xF0, 0xA8, 0x68),
        _ => Color.FromRgb(0x98, 0xAB, 0xC0)
    };
}
