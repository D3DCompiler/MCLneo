using System.Windows;
using System.Windows.Media;

namespace MCLneo.App.Services;

/// <summary>
/// Swaps the application's theme dictionary and accent palette at runtime.
/// Every themed brush in XAML is referenced with DynamicResource, so a swap
/// updates open windows immediately without rebuilding them.
/// </summary>
public static class ThemeService
{
    public sealed record Theme(string Key, string Name, Color LightAccent, Color DarkAccent)
    {
        public override string ToString() => Name;
    }

    public static readonly IReadOnlyList<Theme> Themes = new[]
    {
        new Theme("blue", "钴蓝", Color.FromRgb(0x2F, 0x5B, 0xFF), Color.FromRgb(0x6D, 0x8D, 0xFF)),
        new Theme("orange", "暖橙", Color.FromRgb(0xD3, 0x54, 0x1F), Color.FromRgb(0xFF, 0x7A, 0x45)),
        new Theme("violet", "紫罗兰", Color.FromRgb(0x6B, 0x4D, 0xCC), Color.FromRgb(0xA7, 0x8B, 0xFA)),
        new Theme("slate", "石墨", Color.FromRgb(0x3F, 0x4A, 0x5A), Color.FromRgb(0xC3, 0xCB, 0xD8))
    };

    public static Theme CurrentTheme { get; private set; } = Themes[0];
    public static bool IsDark { get; private set; } = true;
    public static string CurrentMode => IsDark ? "dark" : "light";

    public static event EventHandler? ThemeChanging;
    public static event EventHandler? ThemeChanged;

    private static readonly Dictionary<string, ResourceDictionary> ThemeCache = new(StringComparer.OrdinalIgnoreCase);

    public static void Apply(string? mode, string? accentKey)
    {
        var newDark = !string.Equals(mode, "light", StringComparison.OrdinalIgnoreCase);
        var theme = Themes.FirstOrDefault(t =>
                        string.Equals(t.Key, NormalizeAccent(accentKey), StringComparison.OrdinalIgnoreCase)) ??
                    Themes[0];
        var changed = newDark != IsDark || theme.Key != CurrentTheme.Key;
        var app = Application.Current;
        if (changed && app is not null)
        {
            // Let open windows snapshot the old palette before resources are swapped.
            ThemeChanging?.Invoke(null, EventArgs.Empty);
        }

        IsDark = newDark;
        CurrentTheme = theme;
        if (app is null)
        {
            return;
        }

        var source = new Uri(IsDark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative);
        var cacheKey = IsDark ? "dark" : "light";
        if (!ThemeCache.TryGetValue(cacheKey, out var dictionary))
        {
            dictionary = new ResourceDictionary { Source = source };
            ThemeCache[cacheKey] = dictionary;
        }

        // Copy the palette into the local resource dictionary instead of swapping a
        // merged dictionary. Replacing keys in place is what invalidates existing
        // DynamicResource references in open windows.
        foreach (var key in dictionary.Keys)
        {
            app.Resources[key] = dictionary[key];
        }

        var accent = IsDark ? theme.DarkAccent : theme.LightAccent;
        var hover = IsDark ? Lighten(accent, 0.16) : Darken(accent, 0.16);
        var soft = IsDark ? Mix(accent, Color.FromRgb(0x08, 0x08, 0x0A), 0.86) : Mix(accent, Colors.White, 0.88);
        var border = IsDark ? Mix(accent, Color.FromRgb(0x08, 0x08, 0x0A), 0.62) : Mix(accent, Colors.White, 0.62);
        var ink = IsDark ? Color.FromRgb(0x08, 0x0B, 0x18) : Colors.White;
        var gradientEnd = IsDark ? Lighten(accent, 0.30) : Darken(accent, 0.22);

        app.Resources["AccentBrush"] = new SolidColorBrush(accent);
        app.Resources["AccentHoverBrush"] = new SolidColorBrush(hover);
        app.Resources["AccentSoftBrush"] = new SolidColorBrush(soft);
        app.Resources["AccentBorderBrush"] = new SolidColorBrush(border);
        app.Resources["AccentInkBrush"] = new SolidColorBrush(ink);
        app.Resources["AccentGradientBrush"] = new LinearGradientBrush(accent, gradientEnd, 0);
        app.Resources["SelectionBrush"] = new SolidColorBrush(soft);
        app.Resources["IsDarkTheme"] = IsDark;

        if (changed)
        {
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static void ToggleMode()
    {
        Apply(IsDark ? "light" : "dark", CurrentTheme.Key);
    }

    private static string NormalizeAccent(string? key) => key?.ToLowerInvariant() switch
    {
        "mint" or "moss" or "cyan" or "cobalt" => "blue",
        "persimmon" or "amber" or "ochre" => "orange",
        "violet" => "violet",
        "slate" => "slate",
        "blue" or "orange" => key!.ToLowerInvariant(),
        _ => "blue"
    };

    private static Color Lighten(Color color, double amount) => Color.FromRgb(
        (byte)Math.Clamp(color.R + (255 - color.R) * amount, 0, 255),
        (byte)Math.Clamp(color.G + (255 - color.G) * amount, 0, 255),
        (byte)Math.Clamp(color.B + (255 - color.B) * amount, 0, 255));

    private static Color Darken(Color color, double amount) => Color.FromRgb(
        (byte)Math.Clamp(color.R * (1 - amount), 0, 255),
        (byte)Math.Clamp(color.G * (1 - amount), 0, 255),
        (byte)Math.Clamp(color.B * (1 - amount), 0, 255));

    private static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
        (byte)Math.Clamp(a.R * (1 - amount) + b.R * amount, 0, 255),
        (byte)Math.Clamp(a.G * (1 - amount) + b.G * amount, 0, 255),
        (byte)Math.Clamp(a.B * (1 - amount) + b.B * amount, 0, 255));
}
