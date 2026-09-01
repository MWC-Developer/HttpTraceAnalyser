using System;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using HttpTraceAnalyser.Model;
using ModelContextProtocol.Server;

namespace HttpTraceAnalyser.Mcp
{
    /// <summary>
    /// MCP tools exposing the currently loaded trace and its highlight/filter rule sets to
    /// external MCP clients (e.g. GitHub Copilot CLI). Runs in-process alongside the WPF UI;
    /// all access to UI-owned state is marshaled onto the dispatcher thread.
    /// </summary>
    [McpServerToolType]
    public static class TraceMcpTools
    {
        private static MainWindow? GetMainWindow()
            => Application.Current?.Dispatcher.Invoke(() => Application.Current.MainWindow as MainWindow);

        [McpServerTool, Description("Returns basic info about the currently loaded HTTP trace file (path and row count), or a message if none is loaded.")]
        public static string GetTraceInfo()
        {
            var window = GetMainWindow();
            if (window is null)
                return "No HttpTraceAnalyser window is available.";

            return window.Dispatcher.Invoke(() =>
            {
                var trace = window.Trace;
                return trace is null
                    ? "No trace file is currently loaded."
                    : $"Loaded trace: {trace.FilePath} ({trace.Count} messages).";
            });
        }

        [McpServerTool, Description("Searches the loaded HTTP trace for rows whose URL, host, path, or method contains the given text (case-insensitive) and returns a summary of matching rows.")]
        public static string SearchTrace(
            [Description("Text to search for within URL/host/path/method.")] string searchText,
            [Description("Maximum number of matching rows to return.")] int maxResults = 20)
        {
            var window = GetMainWindow();
            if (window is null)
                return "No HttpTraceAnalyser window is available.";

            return window.Dispatcher.Invoke(() =>
            {
                var trace = window.Trace;
                if (trace is null)
                    return "No trace file is currently loaded.";

                if (string.IsNullOrWhiteSpace(searchText))
                    return "searchText must not be empty.";

                var matches = trace.Messages.AsEnumerable()
                    .Where(row =>
                        RowContains(row, TraceDataSchema.Url, searchText) ||
                        RowContains(row, TraceDataSchema.Host, searchText) ||
                        RowContains(row, TraceDataSchema.Path, searchText) ||
                        RowContains(row, TraceDataSchema.Method, searchText))
                    .Take(maxResults)
                    .Select(row => $"#{row[TraceDataSchema.Index]} {row[TraceDataSchema.Method]} {row[TraceDataSchema.Url]} -> {(row[TraceDataSchema.Response] is int code ? code.ToString() : "(no response)")}")
                    .ToList();

                return matches.Count == 0
                    ? $"No rows matched '{searchText}'."
                    : string.Join(Environment.NewLine, matches);
            });
        }

        private static bool RowContains(DataRow row, string column, string searchText)
            => row[column] is string s && s.Contains(searchText, StringComparison.OrdinalIgnoreCase);

        [McpServerTool, Description("Adds a highlight rule that colors matching rows in the trace grid. Column values: Response, Method, Host, Path, Url, Date, Time. Operator values: Equals, NotEquals, Contains, StartsWith, Regex, Range (value formatted as 'min-max').")]
        public static string HighlightTrace(
            [Description("Column to match against.")] HighlightColumn column,
            [Description("Comparison operator.")] HighlightOperator @operator,
            [Description("Value to compare, or 'min-max' when operator is Range.")] string value,
            [Description("Background color as a hex string, e.g. #FFFF00.")] string backgroundColorHex = "#FFFF00",
            [Description("Optional foreground (text) color as a hex string.")] string? foregroundColorHex = null)
        {
            var window = GetMainWindow();
            if (window is null)
                return "No HttpTraceAnalyser window is available.";

            return window.Dispatcher.Invoke(() =>
            {
                Color background;
                try
                {
                    background = (Color)ColorConverter.ConvertFromString(backgroundColorHex)!;
                }
                catch (Exception ex)
                {
                    return $"Invalid backgroundColorHex '{backgroundColorHex}': {ex.Message}";
                }

                Color? foreground = null;
                if (!string.IsNullOrWhiteSpace(foregroundColorHex))
                {
                    try
                    {
                        foreground = (Color)ColorConverter.ConvertFromString(foregroundColorHex)!;
                    }
                    catch (Exception ex)
                    {
                        return $"Invalid foregroundColorHex '{foregroundColorHex}': {ex.Message}";
                    }
                }

                var rule = new HighlightRule
                {
                    Column = column,
                    Operator = @operator,
                    Value = value,
                    BackgroundColor = background,
                    ForegroundColor = foreground,
                };
                HighlightRuleSet.Rules.Add(rule);
                return $"Added highlight rule: {column} {@operator} '{value}' (background {backgroundColorHex}).";
            });
        }

        [McpServerTool, Description("Removes all highlight rules currently applied to the trace grid.")]
        public static string ClearHighlights()
        {
            var window = GetMainWindow();
            if (window is null)
                return "No HttpTraceAnalyser window is available.";

            return window.Dispatcher.Invoke(() =>
            {
                var count = HighlightRuleSet.Rules.Count;
                HighlightRuleSet.Rules.Clear();
                return $"Removed {count} highlight rule(s).";
            });
        }

        [McpServerTool, Description("Adds a filter rule restricting which rows are visible in the trace grid. Field values: Response, Method, Host, Path, Url, Date, Time. Comparator values: Equals, NotEquals, Contains, StartsWith, Range (value formatted as 'min-max'). Combinator (And/Or) determines how this rule combines with previously added rules.")]
        public static string FilterTrace(
            [Description("Field to match against.")] FilterField field,
            [Description("Comparison operator.")] FilterComparator comparator,
            [Description("Value to compare, or 'min-max' when comparator is Range.")] string value,
            [Description("Logical combinator with previously added rules.")] FilterCombinator combinator = FilterCombinator.And)
        {
            var window = GetMainWindow();
            if (window is null)
                return "No HttpTraceAnalyser window is available.";

            return window.Dispatcher.Invoke(() =>
            {
                FilterRuleSet.Rules.Add(new FilterRule
                {
                    Field = field,
                    Comparator = comparator,
                    Value = value,
                    Combinator = combinator,
                });
                return $"Added filter: {combinator} {field} {comparator} '{value}'.";
            });
        }

        [McpServerTool, Description("Removes all active filter rules, showing every row in the trace grid again.")]
        public static string ClearFilters()
        {
            var window = GetMainWindow();
            if (window is null)
                return "No HttpTraceAnalyser window is available.";

            return window.Dispatcher.Invoke(() =>
            {
                var count = FilterRuleSet.Rules.Count;
                FilterRuleSet.Rules.Clear();
                return $"Removed {count} filter rule(s).";
            });
        }

        [McpServerTool, Description("Selects the trace row with the given index (as shown in the grid's Index column) so it is shown in the request/response viewers. Fails if the row is hidden by an active filter; use ClearFilters or FindAndSelectTraceRow first if needed.")]
        public static string SelectTraceRow(
            [Description("The Index column value of the row to select.")] int index)
        {
            var window = GetMainWindow();
            if (window is null)
                return "No HttpTraceAnalyser window is available.";

            return window.Dispatcher.Invoke(() => window.SelectTraceRow(index));
        }

        [McpServerTool, Description("Finds the first row across the whole loaded trace (ignoring any active filter) matching the given field/comparator/value, selects it, and shows it in the request/response viewers. Field values: Response, Method, Host, Path, Url, Date, Time. Comparator values: Equals, NotEquals, Contains, StartsWith, Range (value formatted as 'min-max'). Useful for e.g. jumping straight to the first row with a specific error status code.")]
        public static string FindAndSelectTraceRow(
            [Description("Field to match against.")] FilterField field,
            [Description("Comparison operator.")] FilterComparator comparator,
            [Description("Value to compare, or 'min-max' when comparator is Range.")] string value)
        {
            var window = GetMainWindow();
            if (window is null)
                return "No HttpTraceAnalyser window is available.";

            return window.Dispatcher.Invoke(() => window.FindAndSelectTraceRow(field, comparator, value));
        }
    }
}
