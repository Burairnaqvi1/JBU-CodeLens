using System.Windows;
using System.Windows.Media;

namespace CodeLensAI.UI.Helpers;

public enum AppTheme
{
    Dark,
    Light,
}

/// <summary>
/// Switches between the Dark/Light palettes defined in <c>Theme/*.xaml</c>. Every
/// <c>*Color</c> resource in the selected dictionary is applied to the matching app-level
/// <c>*Brush</c> — mutating the existing brush's <c>Color</c> in place where possible (so
/// code-built elements holding a brush instance update too) and replacing the resource when
/// the brush is frozen (safe because all XAML consumers use <c>DynamicResource</c> and
/// long-lived code-built elements use <c>SetResourceReference</c>).
/// No color values live in code — they come exclusively from the theme dictionaries.
/// </summary>
public static class ThemeManager
{
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    /// <summary>Raised after a theme has been applied, so views can re-render themed content.</summary>
    public static event Action<AppTheme>? ThemeChanged;

    public static void Apply(AppTheme theme)
    {
        var source = theme == AppTheme.Dark
            ? new Uri("/Theme/DarkTheme.xaml", UriKind.Relative)
            : new Uri("/Theme/LightTheme.xaml", UriKind.Relative);
        var palette = new ResourceDictionary { Source = source };
        var appResources = Application.Current.Resources;

        foreach (var key in palette.Keys)
        {
            if (key is not string colorKey ||
                !colorKey.EndsWith("Color", StringComparison.Ordinal) ||
                palette[colorKey] is not Color color)
            {
                continue;
            }

            var brushKey = string.Concat(colorKey.AsSpan(0, colorKey.Length - "Color".Length), "Brush");
            if (appResources[brushKey] is SolidColorBrush { IsFrozen: false } existing)
            {
                existing.Color = color;
            }
            else
            {
                appResources[brushKey] = new SolidColorBrush(color);
            }
        }

        // The accent gradient is the one non-solid brush; retarget its stops from the palette
        // the same way the solid brushes are mutated (all consumers use DynamicResource).
        if (palette["PrimaryColor"] is Color primary && palette["AccentAltColor"] is Color accentAlt)
        {
            if (appResources["AccentGradientBrush"] is LinearGradientBrush { IsFrozen: false } gradient &&
                gradient.GradientStops.Count == 2)
            {
                gradient.GradientStops[0].Color = primary;
                gradient.GradientStops[1].Color = accentAlt;
            }
            else
            {
                appResources["AccentGradientBrush"] =
                    new LinearGradientBrush(primary, accentAlt, new Point(0, 0), new Point(1, 1));
            }
        }

        Current = theme;
        ThemeChanged?.Invoke(theme);
    }
}
