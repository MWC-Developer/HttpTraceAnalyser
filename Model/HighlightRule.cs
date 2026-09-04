using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace HttpTraceAnalyser.Model
{
    public enum HighlightColumn
    {
        Response,
        Method,
        Host,
        Path,
        Url,
        Date,
        Time,
        Index,
        ReasonPhrase,
        Latency,
        ContentType,
        ClientRequestId,
        SoapMethod,
        XRequestId,
        /// <summary>
        /// A plugin-contributed extended field. The actual column name is held in
        /// <see cref="HighlightRule.CustomFieldName"/> (see <see cref="HttpTraceFile.ExtendedFieldNames"/>).
        /// </summary>
        Custom,
    }

    public enum HighlightOperator
    {
        Equals,
        NotEquals,
        Contains,
        StartsWith,
        Regex,
        /// <summary>Value formatted as "min-max" (inclusive) for numeric columns.</summary>
        Range,
    }

    /// <summary>
    /// A single row-highlighting rule matched against a property (column) of the list item.
    /// </summary>
    public sealed class HighlightRule : INotifyPropertyChanged
    {
        private bool _isEnabled = true;
        private HighlightColumn _column = HighlightColumn.Response;
        private HighlightOperator _operator = HighlightOperator.Equals;
        private string _value = string.Empty;
        private string _customFieldName = string.Empty;
        private Color _backgroundColor = Colors.Transparent;
        private Color? _foregroundColor;

        public bool IsEnabled
        {
            get => _isEnabled;
            set => Set(ref _isEnabled, value);
        }

        public HighlightColumn Column
        {
            get => _column;
            set => Set(ref _column, value);
        }

        public HighlightOperator Operator
        {
            get => _operator;
            set => Set(ref _operator, value);
        }

        public string Value
        {
            get => _value;
            set => Set(ref _value, value ?? string.Empty);
        }

        /// <summary>
        /// Name of the plugin-contributed extended field to highlight on, used when
        /// <see cref="Column"/> is <see cref="HighlightColumn.Custom"/>. Must match a name in
        /// <see cref="HttpTraceFile.ExtendedFieldNames"/>.
        /// </summary>
        public string CustomFieldName
        {
            get => _customFieldName;
            set => Set(ref _customFieldName, value ?? string.Empty);
        }

        public Color BackgroundColor
        {
            get => _backgroundColor;
            set => Set(ref _backgroundColor, value);
        }

        public Color? ForegroundColor
        {
            get => _foregroundColor;
            set => Set(ref _foregroundColor, value);
        }

        public bool Matches(object? candidateValue)
        {
            if (!IsEnabled)
                return false;

            var text = candidateValue?.ToString() ?? string.Empty;

            switch (Operator)
            {
                case HighlightOperator.Equals:
                    return string.Equals(text, Value, StringComparison.OrdinalIgnoreCase);
                case HighlightOperator.NotEquals:
                    return !string.Equals(text, Value, StringComparison.OrdinalIgnoreCase);
                case HighlightOperator.Contains:
                    return text.Contains(Value, StringComparison.OrdinalIgnoreCase);
                case HighlightOperator.StartsWith:
                    return text.StartsWith(Value, StringComparison.OrdinalIgnoreCase);
                case HighlightOperator.Regex:
                    try { return Regex.IsMatch(text, Value, RegexOptions.IgnoreCase); }
                    catch { return false; }
                case HighlightOperator.Range:
                    return MatchesRange(candidateValue, text);
                default:
                    return false;
            }
        }

        private bool MatchesRange(object? candidateValue, string text)
        {
            var dash = Value.IndexOf('-');
            if (dash <= 0 || dash == Value.Length - 1)
                return false;
            if (!double.TryParse(Value[..dash], NumberStyles.Any, CultureInfo.InvariantCulture, out var min) ||
                !double.TryParse(Value[(dash + 1)..], NumberStyles.Any, CultureInfo.InvariantCulture, out var max))
            {
                return false;
            }

            double number;
            if (candidateValue is IConvertible)
            {
                try { number = Convert.ToDouble(candidateValue, CultureInfo.InvariantCulture); }
                catch { return false; }
            }
            else if (!double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out number))
            {
                return false;
            }

            return number >= min && number <= max;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value))
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
            HighlightRuleSet.NotifyRuleChanged(this);
        }
    }

    /// <summary>Central store of highlight rules; evaluated in order (first match wins).</summary>
    public static class HighlightRuleSet
    {
        public static ObservableCollection<HighlightRule> Rules { get; } = new();

        public static event EventHandler? RulesChanged;

        static HighlightRuleSet()
        {
            AddDefaults();
            Rules.CollectionChanged += OnCollectionChanged;
        }

        private static void AddDefaults()
        {
            Rules.Add(new HighlightRule
            {
                Column = HighlightColumn.Response,
                Operator = HighlightOperator.Equals,
                Value = "429",
                BackgroundColor = Color.FromRgb(0xFF, 0xF3, 0xB0), // light yellow
            });
            Rules.Add(new HighlightRule
            {
                Column = HighlightColumn.Response,
                Operator = HighlightOperator.Range,
                Value = "200-299",
                BackgroundColor = Color.FromRgb(0xD4, 0xF7, 0xD4), // light green
                IsEnabled = false,
            });
            Rules.Add(new HighlightRule
            {
                Column = HighlightColumn.Response,
                Operator = HighlightOperator.Range,
                Value = "400-599",
                BackgroundColor = Color.FromRgb(0xF7, 0xC8, 0xC8), // light red
            });
        }

        private static void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems is not null)
            {
                foreach (var r in e.OldItems)
                    if (r is HighlightRule rule)
                        rule.PropertyChanged -= OnRulePropertyChanged;
            }
            if (e.NewItems is not null)
            {
                foreach (var r in e.NewItems)
                    if (r is HighlightRule rule)
                        rule.PropertyChanged += OnRulePropertyChanged;
            }
            RaiseChanged();
        }

        private static void OnRulePropertyChanged(object? sender, PropertyChangedEventArgs e) => RaiseChanged();

        internal static void NotifyRuleChanged(HighlightRule rule) => RaiseChanged();

        private static void RaiseChanged() => RulesChanged?.Invoke(null, EventArgs.Empty);

        /// <summary>Finds the first enabled rule that matches the given item; null if none.</summary>
        public static HighlightRule? Match(object? item)
        {
            if (item is null)
                return null;

            foreach (var rule in Rules)
            {
                if (!rule.IsEnabled)
                    continue;

                var value = GetColumnValue(item, rule);
                if (rule.Matches(value))
                    return rule;
            }
            return null;
        }

        public static Brush? GetBackground(object? item)
        {
            var rule = Match(item);
            if (rule is null)
                return null;
            var brush = new SolidColorBrush(rule.BackgroundColor);
            if (brush.CanFreeze)
                brush.Freeze();
            return brush;
        }

        public static Brush? GetForeground(object? item)
        {
            var rule = Match(item);
            if (rule?.ForegroundColor is null)
                return null;
            var brush = new SolidColorBrush(rule.ForegroundColor.Value);
            if (brush.CanFreeze)
                brush.Freeze();
            return brush;
        }

        private static object? GetColumnValue(object item, HighlightRule rule)
        {
            var name = rule.Column == HighlightColumn.Custom ? rule.CustomFieldName : rule.Column.ToString();
            if (string.IsNullOrEmpty(name))
                return null;

            switch (item)
            {
                case System.Data.DataRowView drv:
                    return ReadCell(drv.Row, name);
                case System.Data.DataRow row:
                    return ReadCell(row, name);
            }

            var prop = item.GetType().GetProperty(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return prop?.GetValue(item);
        }

        private static object? ReadCell(System.Data.DataRow row, string columnName)
        {
            if (!row.Table.Columns.Contains(columnName))
                return null;
            var value = row[columnName];
            return value is DBNull ? null : value;
        }
    }
}
