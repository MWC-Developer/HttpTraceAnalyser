using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using HttpTraceAnalyser.Model;

namespace HttpTraceAnalyser
{
    public partial class HighlightsWindow : Window
    {
        public static IValueConverter ColorConverter { get; } = new ColorHexConverter(nullable: false);
        public static IValueConverter NullableColorConverter { get; } = new ColorHexConverter(nullable: true);

        public HighlightsWindow()
        {
            InitializeComponent();
            RulesGrid.ItemsSource = HighlightRuleSet.Rules;
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            HighlightRuleSet.Rules.Add(new HighlightRule
            {
                Column = HighlightColumn.Response,
                Operator = HighlightOperator.Equals,
                Value = string.Empty,
                BackgroundColor = Colors.LightGray,
            });
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = new System.Collections.Generic.List<HighlightRule>();
            foreach (var item in RulesGrid.SelectedItems)
            {
                if (item is HighlightRule rule)
                    selected.Add(rule);
            }
            foreach (var rule in selected)
                HighlightRuleSet.Rules.Remove(rule);
        }

        private sealed class ColorHexConverter : IValueConverter
        {
            private readonly bool _nullable;

            public ColorHexConverter(bool nullable) => _nullable = nullable;

            public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            {
                if (value is Color c)
                    return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                return string.Empty;
            }

            public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            {
                var text = value?.ToString()?.Trim();
                if (string.IsNullOrEmpty(text))
                    return _nullable ? (object?)null : Colors.Transparent;

                try
                {
                    var obj = ColorConverterFromString(text);
                    return obj;
                }
                catch
                {
                    return _nullable
                        ? (object?)Binding.DoNothing
                        : Binding.DoNothing;
                }
            }

            private static Color ColorConverterFromString(string text)
            {
                var obj = System.Windows.Media.ColorConverter.ConvertFromString(text);
                if (obj is Color color)
                    return color;
                throw new FormatException();
            }
        }
    }
}
