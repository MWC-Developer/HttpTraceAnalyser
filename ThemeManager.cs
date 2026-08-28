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
    /// Detects and tracks the system theme. Theme colors are now automatically
    /// handled by SystemColors in the resource dictionaries.
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

            // Force UI refresh to apply system color changes
            app.MainWindow?.InvalidateVisual();
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
    ///  * Otherwise, if the row has a highlight background, automatically choose
    ///    black or white based on the background brightness to ensure readability.
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

            if (values.Length > 1 && values[1] is SolidColorBrush backgroundBrush)
                return GetContrastingForeground(backgroundBrush.Color);

            return Application.Current?.TryFindResource("WindowForegroundBrush") as Brush
                ?? SystemColors.ControlTextBrush;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();

        /// <summary>
        /// Returns a contrasting foreground brush (black or white) based on the brightness
        /// of the given background color, ensuring text remains readable.
        /// </summary>
        private static Brush GetContrastingForeground(Color backgroundColor)
        {
            // Calculate relative luminance using the standard formula (Rec. 709)
            // https://www.w3.org/TR/WCAG20/#relativeluminancedef
            double r = backgroundColor.R / 255.0;
            double g = backgroundColor.G / 255.0;
            double b = backgroundColor.B / 255.0;

            // Apply gamma correction
            r = r <= 0.03928 ? r / 12.92 : Math.Pow((r + 0.055) / 1.055, 2.4);
            g = g <= 0.03928 ? g / 12.92 : Math.Pow((g + 0.055) / 1.055, 2.4);
            b = b <= 0.03928 ? b / 12.92 : Math.Pow((b + 0.055) / 1.055, 2.4);

            double luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;

            // Use white text for dark backgrounds (luminance < 0.5), black for light backgrounds
            return luminance < 0.5 ? Brushes.White : Brushes.Black;
        }
    }
}
