using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HttpTraceAnalyser
{
    /// <summary>
    /// Themed in-app replacement for the unthemed Win32 <c>ChooseColor</c> common dialog.
    /// Offers a swatch palette, hex entry, and a Clear option for rules that should fall back
    /// to an automatic colour.
    /// </summary>
    public partial class ColorPickerWindow : Window
    {
        /// <summary>The chosen colour, or <c>null</c> when the user pressed Clear.</summary>
        public Color? SelectedColor { get; private set; }

        private bool _updatingHex;

        public ColorPickerWindow(Color? initialColor, bool canClear)
        {
            InitializeComponent();

            SwatchList.ItemsSource = BuildPalette();
            ClearButton.Visibility = canClear ? Visibility.Visible : Visibility.Collapsed;

            SelectedColor = initialColor;
            SetHexText(initialColor ?? Colors.White);
        }

        /// <summary>
        /// Must be public: WPF data binding resolves properties by reflection and cannot bind to
        /// members of a non-public type, which silently yields empty (black) swatches.
        /// </summary>
        public sealed record Swatch(string Name, Color Color);

        private static IReadOnlyList<Swatch> BuildPalette()
        {
            var swatches = new List<Swatch>();

            // Greyscale ramp.
            for (int i = 0; i < 10; i++)
            {
                byte level = (byte)(255 - (i * 255 / 9));
                swatches.Add(new Swatch($"Grey {level}", Color.FromRgb(level, level, level)));
            }

            // Hue ramp at several lightness levels, giving pastel through saturated rows.
            double[] lightnessLevels = [0.85, 0.70, 0.50, 0.35];
            foreach (double lightness in lightnessLevels)
            {
                for (int i = 0; i < 10; i++)
                {
                    double hue = i * 360.0 / 10.0;
                    var color = FromHsl(hue, 0.75, lightness);
                    swatches.Add(new Swatch(FormatColor(color), color));
                }
            }

            return swatches;
        }

        private static Color FromHsl(double hue, double saturation, double lightness)
        {
            double c = (1 - Math.Abs((2 * lightness) - 1)) * saturation;
            double x = c * (1 - Math.Abs((hue / 60.0 % 2) - 1));
            double m = lightness - (c / 2);

            (double r, double g, double b) = hue switch
            {
                < 60 => (c, x, 0d),
                < 120 => (x, c, 0d),
                < 180 => (0d, c, x),
                < 240 => (0d, x, c),
                < 300 => (x, 0d, c),
                _ => (c, 0d, x),
            };

            return Color.FromRgb(
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255));
        }

        internal static string FormatColor(Color color)
            => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        private void Swatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: Swatch swatch })
                SetHexText(swatch.Color);
        }

        private void SetHexText(Color color)
        {
            _updatingHex = true;
            try
            {
                HexTextBox.Text = FormatColor(color);
            }
            finally
            {
                _updatingHex = false;
            }

            ApplyColor(color);
        }

        private void ApplyColor(Color color)
        {
            SelectedColor = color;
            PreviewBorder.Background = new SolidColorBrush(color);
            HexErrorText.Visibility = Visibility.Collapsed;
            OkButton.IsEnabled = true;
        }

        private void HexTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_updatingHex)
                return;

            if (TryParseColor(HexTextBox.Text, out var color))
            {
                ApplyColor(color);
            }
            else
            {
                HexErrorText.Visibility = Visibility.Visible;
                OkButton.IsEnabled = false;
            }
        }

        private static bool TryParseColor(string? text, out Color color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var value = text.Trim();
            if (!value.StartsWith('#'))
                value = "#" + value;

            // ColorConverter accepts named colours too, but only #RGB forms are round-tripped here.
            if (value.Length is not (4 or 7 or 9))
                return false;

            try
            {
                if (ColorConverter.ConvertFromString(value) is Color parsed)
                {
                    color = parsed;
                    return true;
                }
            }
            catch (FormatException)
            {
            }

            return false;
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedColor = null;
            DialogResult = true;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParseColor(HexTextBox.Text, out var color))
                return;

            SelectedColor = color;
            DialogResult = true;
        }
    }
}
