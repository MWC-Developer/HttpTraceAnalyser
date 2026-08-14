using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;

namespace HttpTraceAnalyser
{
    public enum AppTheme
    {
        Light,
        Dark,
    }

    /// <summary>
    /// Manages the palette resource dictionary that drives the app's light/dark theme.
    /// </summary>
    public static class ThemeManager
    {
        private static readonly Uri LightUri = new("Themes/Light.xaml", UriKind.Relative);
        private static readonly Uri DarkUri = new("Themes/Dark.xaml", UriKind.Relative);

        public static AppTheme Current { get; private set; } = AppTheme.Light;

        public static event EventHandler? ThemeChanged;

        public static AppTheme GetSystemTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("AppsUseLightTheme") is int v && v == 0)
                    return AppTheme.Dark;
            }
            catch
            {
                // Fall through to light default on any error.
            }

            return AppTheme.Light;
        }

        public static void Apply(AppTheme theme)
        {
            var app = Application.Current;
            if (app is null)
                return;

            var uri = theme == AppTheme.Dark ? DarkUri : LightUri;
            var newDict = new ResourceDictionary { Source = uri };

            var merged = app.Resources.MergedDictionaries;
            int paletteIndex = -1;
            for (int i = 0; i < merged.Count; i++)
            {
                var src = merged[i].Source;
                if (src is not null &&
                    (src.OriginalString.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                     src.OriginalString.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase)))
                {
                    paletteIndex = i;
                    break;
                }
            }

            if (paletteIndex >= 0)
                merged[paletteIndex] = newDict;
            else
                merged.Insert(0, newDict);

            Current = theme;
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Substitutes the current theme's foreground brush when a row's foreground binding is null.
    /// </summary>
    public sealed class RowForegroundConverter : IValueConverter
    {
        public static readonly RowForegroundConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is Brush brush)
                return brush;

            return Application.Current?.TryFindResource("WindowForegroundBrush") as Brush
                ?? SystemColors.ControlTextBrush;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Resolves the effective foreground brush for a highlighted row:
    ///  * If the row has an explicit foreground, use it.
    ///  * Otherwise, if the row has a highlight background, force a dark
    ///    foreground so text stays legible against light highlight colours
    ///    even when the app is in dark mode.
    ///  * Otherwise, fall back to the current theme's window foreground.
    /// Expects two bound values: [0] RowForeground, [1] RowBackground.
    /// </summary>
    public sealed class RowForegroundMultiConverter : IMultiValueConverter
    {
        public static readonly RowForegroundMultiConverter Instance = new();

        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Length > 0 && values[0] is Brush explicitForeground)
                return explicitForeground;

            if (values.Length > 1 && values[1] is Brush)
                return Brushes.Black;

            return Application.Current?.TryFindResource("WindowForegroundBrush") as Brush
                ?? SystemColors.ControlTextBrush;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
