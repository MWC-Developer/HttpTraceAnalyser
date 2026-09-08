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
        Process,
        ContentType,
        ClientRequestId,
        SoapMethod,
        XRequestId,
        /// <summary>
        /// A dynamically named field. The actual column name is held in
        /// <see cref="HighlightRule.CustomFieldName"/>.
        /// </summary>
        Custom,
    }

    public enum HighlightOperator
    {
        Equals,
        NotEquals,
        Contains,
        DoesNotContain,
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
            set
            {
                if (Set(ref _column, value))
                    OnPropertyChanged(nameof(ColumnName));
            }
        }

        public string ColumnName
        {
            get => Column == HighlightColumn.Custom ? CustomFieldName : Column.ToString();
            set
            {
                var name = value ?? string.Empty;
                if (Enum.TryParse<HighlightColumn>(name, ignoreCase: true, out var column) &&
                    column != HighlightColumn.Custom)
                {
                    Column = column;
                    CustomFieldName = string.Empty;
                }
                else
                {
                    CustomFieldName = name;
                    Column = HighlightColumn.Custom;
                }
            }
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
        /// Name of the dynamically named field to highlight on, used when
        /// <see cref="Column"/> is <see cref="HighlightColumn.Custom"/>.
        /// </summary>
        public string CustomFieldName
        {
            get => _customFieldName;
            set
            {
                if (Set(ref _customFieldName, value ?? string.Empty) && Column == HighlightColumn.Custom)
                    OnPropertyChanged(nameof(ColumnName));
            }
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
                case HighlightOperator.DoesNotContain:
                    return !text.Contains(Value, StringComparison.OrdinalIgnoreCase);
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

        internal HighlightRule Clone() => new()
        {
            IsEnabled = IsEnabled,
            Column = Column,
            Operator = Operator,
            Value = Value,
            CustomFieldName = CustomFieldName,
            BackgroundColor = BackgroundColor,
            ForegroundColor = ForegroundColor,
        };

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value))
                return false;
            field = value;
            OnPropertyChanged(name!);
            return true;
        }

        private void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Central store of highlight rules; evaluated in order (first match wins).</summary>
    /// <summary>
    /// Holds a session's active highlight rules, evaluated in order (first match wins). Each
    /// loaded trace session owns its own independent instance (see
    /// <see cref="TraceSession.Highlights"/>) so highlight rules on one trace never affect another.
    /// </summary>
    public sealed class HighlightRuleCollection
    {
        private bool _suppressNotifications;

        public ObservableCollection<HighlightRule> Rules { get; } = new();

        public event EventHandler? RulesChanged;

        public HighlightRuleCollection()
        {
            Rules.CollectionChanged += OnCollectionChanged;
            ResetToDefault();
        }

        public void ResetToDefault()
        {
            if (System.IO.File.Exists(RulePersistence.HighlightDefaultPath))
            {
                try
                {
                    ReplaceRules(RulePersistence.LoadHighlights(RulePersistence.HighlightDefaultPath));
                    return;
                }
                catch
                {
                    // A bad user default must not prevent application startup.
                }
            }

            ReplaceRules(CreateFactoryDefaults());
        }

        /// <summary>Replaces the active rules with the given set, e.g. after a dialog commits its draft.</summary>
        public void Replace(System.Collections.Generic.IEnumerable<HighlightRule> rules) => ReplaceRules(rules);

        /// <summary>Returns the saved default rules, or the built-in factory defaults if none are saved, without applying them.</summary>
        internal static List<HighlightRule> GetSavedDefaultOrFactory()
        {
            if (System.IO.File.Exists(RulePersistence.HighlightDefaultPath))
            {
                try
                {
                    return RulePersistence.LoadHighlights(RulePersistence.HighlightDefaultPath);
                }
                catch
                {
                    // A bad user default falls back to the factory defaults below.
                }
            }

            return CreateFactoryDefaults();
        }

        /// <summary>Returns the built-in factory default rules without applying them.</summary>
        internal static List<HighlightRule> GetFactoryDefaults() => CreateFactoryDefaults();

        private static List<HighlightRule> CreateFactoryDefaults() =>
        [
            new HighlightRule
            {
                Column = HighlightColumn.Response,
                Operator = HighlightOperator.Equals,
                Value = "429",
                BackgroundColor = Color.FromRgb(0xFF, 0xF3, 0xB0),
            },
            new HighlightRule
            {
                Column = HighlightColumn.Response,
                Operator = HighlightOperator.Range,
                Value = "200-299",
                BackgroundColor = Color.FromRgb(0xD4, 0xF7, 0xD4),
                IsEnabled = false,
            },
            new HighlightRule
            {
                Column = HighlightColumn.Response,
                Operator = HighlightOperator.Range,
                Value = "400-599",
                BackgroundColor = Color.FromRgb(0xF7, 0xC8, 0xC8),
            },
        ];

        private void ReplaceRules(IEnumerable<HighlightRule> rules)
        {
            _suppressNotifications = true;
            try
            {
                Rules.Clear();
                foreach (var rule in rules)
                    Rules.Add(rule);
            }
            finally
            {
                _suppressNotifications = false;
            }
            RaiseChanged();
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
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

        private void OnRulePropertyChanged(object? sender, PropertyChangedEventArgs e) => RaiseChanged();

        private void RaiseChanged()
        {
            if (!_suppressNotifications)
                RulesChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Finds the first enabled rule that matches the given item; null if none.</summary>
        public HighlightRule? Match(object? item)
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

        public Brush? GetBackground(object? item)
        {
            var rule = Match(item);
            if (rule is null)
                return null;
            var brush = new SolidColorBrush(rule.BackgroundColor);
            if (brush.CanFreeze)
                brush.Freeze();
            return brush;
        }

        public Brush? GetForeground(object? item)
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

    /// <summary>
    /// Central static façade over the currently active trace session's <see cref="HighlightRuleCollection"/>
    /// (see <see cref="TraceSessionManager.GetActive"/>). Existing callers keep using this static class
    /// exactly as before; it now transparently forwards to whichever session is shown, or a standalone
    /// fallback instance when no session is loaded (e.g. at application startup).
    /// </summary>
    public static class HighlightRuleSet
    {
        private static readonly HighlightRuleCollection FallbackCollection = new();

        private static HighlightRuleCollection Current
            => TraceSessionManager.GetActive()?.Highlights ?? FallbackCollection;

        public static ObservableCollection<HighlightRule> Rules => Current.Rules;

        public static event EventHandler? RulesChanged
        {
            add => Current.RulesChanged += value;
            remove => Current.RulesChanged -= value;
        }

        public static void ResetToDefault() => Current.ResetToDefault();

        /// <summary>Replaces the active rules with the given set, e.g. after a dialog commits its draft.</summary>
        public static void Replace(System.Collections.Generic.IEnumerable<HighlightRule> rules) => Current.Replace(rules);

        /// <summary>Returns the saved default rules, or the built-in factory defaults if none are saved, without applying them.</summary>
        internal static List<HighlightRule> GetSavedDefaultOrFactory() => HighlightRuleCollection.GetSavedDefaultOrFactory();

        /// <summary>Returns the built-in factory default rules without applying them.</summary>
        internal static List<HighlightRule> GetFactoryDefaults() => HighlightRuleCollection.GetFactoryDefaults();

        /// <summary>Finds the first enabled rule that matches the given item; null if none.</summary>
        public static HighlightRule? Match(object? item) => Current.Match(item);

        public static Brush? GetBackground(object? item) => Current.GetBackground(item);

        public static Brush? GetForeground(object? item) => Current.GetForeground(item);
    }
}
