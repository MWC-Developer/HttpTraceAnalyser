using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace HttpTraceAnalyser.Model
{
    /// <summary>Field a <see cref="FilterRule"/> is evaluated against.</summary>
    public enum FilterField
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
    }

    /// <summary>Comparison operator used by a <see cref="FilterRule"/>.</summary>
    public enum FilterComparator
    {
        Equals,
        NotEquals,
        Contains,
        StartsWith,
        /// <summary>Numeric inclusive range, formatted as "min-max".</summary>
        Range,
    }

    /// <summary>Logical operator applied between adjacent rules (ignored on the first rule).</summary>
    public enum FilterCombinator
    {
        And,
        Or,
    }

    /// <summary>A single row-filter rule; combined into a <see cref="System.Data.DataView.RowFilter"/> string.</summary>
    public sealed class FilterRule : INotifyPropertyChanged
    {
        private FilterCombinator _combinator = FilterCombinator.And;
        private FilterField _field = FilterField.Response;
        private FilterComparator _comparator = FilterComparator.Equals;
        private string _value = string.Empty;

        public FilterCombinator Combinator
        {
            get => _combinator;
            set => Set(ref _combinator, value);
        }

        public FilterField Field
        {
            get => _field;
            set => Set(ref _field, value);
        }

        public FilterComparator Comparator
        {
            get => _comparator;
            set => Set(ref _comparator, value);
        }

        public string Value
        {
            get => _value;
            set => Set(ref _value, value ?? string.Empty);
        }

        /// <summary>
        /// Builds the DataView expression fragment for this rule (without the leading combinator).
        /// Returns an empty string if the rule is not evaluable.
        /// </summary>
        internal string BuildExpression()
        {
            var column = Field.ToString();
            var isNumeric = Field is FilterField.Response or FilterField.Index or FilterField.Latency;

            switch (Comparator)
            {
                case FilterComparator.Equals:
                    return isNumeric && TryParseInt(Value, out var eq)
                        ? $"[{column}] = {eq}"
                        : $"[{column}] = {QuoteString(Value)}";

                case FilterComparator.NotEquals:
                    return isNumeric && TryParseInt(Value, out var ne)
                        ? $"[{column}] <> {ne}"
                        : $"[{column}] <> {QuoteString(Value)}";

                case FilterComparator.Contains:
                    return $"[{column}] LIKE {QuoteString("%" + EscapeLike(Value) + "%")}";

                case FilterComparator.StartsWith:
                    return $"[{column}] LIKE {QuoteString(EscapeLike(Value) + "%")}";

                case FilterComparator.Range:
                    return BuildRangeExpression(column, isNumeric);
            }
            return string.Empty;
        }

        private string BuildRangeExpression(string column, bool isNumeric)
        {
            var dash = Value.IndexOf('-');
            if (dash <= 0 || dash == Value.Length - 1)
                return string.Empty;

            var minText = Value[..dash];
            var maxText = Value[(dash + 1)..];

            if (isNumeric)
            {
                if (!TryParseInt(minText, out var min) || !TryParseInt(maxText, out var max))
                    return string.Empty;
                return $"([{column}] >= {min} AND [{column}] <= {max})";
            }

            return $"([{column}] >= {QuoteString(minText)} AND [{column}] <= {QuoteString(maxText)})";
        }

        private static bool TryParseInt(string text, out int value)
            => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

        private static string QuoteString(string s) => "'" + s.Replace("'", "''") + "'";

        private static string EscapeLike(string s)
            => s.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value))
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
            FilterRuleSet.NotifyRuleChanged(this);
        }
    }

    /// <summary>Central store of active filter rules; combined left-to-right using each rule's <see cref="FilterRule.Combinator"/>.</summary>
    public static class FilterRuleSet
    {
        public static ObservableCollection<FilterRule> Rules { get; } = new();

        public static event EventHandler? FiltersChanged;

        static FilterRuleSet()
        {
            Rules.CollectionChanged += OnCollectionChanged;
        }

        private static void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems is not null)
            {
                foreach (var r in e.OldItems)
                    if (r is FilterRule rule)
                        rule.PropertyChanged -= OnRulePropertyChanged;
            }
            if (e.NewItems is not null)
            {
                foreach (var r in e.NewItems)
                    if (r is FilterRule rule)
                        rule.PropertyChanged += OnRulePropertyChanged;
            }
            RaiseChanged();
        }

        private static void OnRulePropertyChanged(object? sender, PropertyChangedEventArgs e) => RaiseChanged();

        internal static void NotifyRuleChanged(FilterRule rule) => RaiseChanged();

        private static void RaiseChanged() => FiltersChanged?.Invoke(null, EventArgs.Empty);

        /// <summary>
        /// Builds a <see cref="System.Data.DataView.RowFilter"/> expression that combines all rules
        /// left-to-right using each rule's <see cref="FilterRule.Combinator"/>.
        /// Returns an empty string when there are no rules.
        /// </summary>
        public static string BuildRowFilter()
        {
            if (Rules.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            bool first = true;
            foreach (var rule in Rules)
            {
                var expr = rule.BuildExpression();
                if (string.IsNullOrEmpty(expr))
                    continue;

                if (first)
                {
                    sb.Append('(').Append(expr).Append(')');
                    first = false;
                }
                else
                {
                    // Left-fold to keep predictable left-to-right precedence: ((a op b) op c) op d.
                    sb.Insert(0, '(').Append(')');
                    sb.Append(' ')
                      .Append(rule.Combinator == FilterCombinator.Or ? "OR" : "AND")
                      .Append(' ')
                      .Append('(').Append(expr).Append(')');
                }
            }
            return sb.ToString();
        }
    }
}
