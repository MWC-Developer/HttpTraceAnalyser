using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
        /// <summary>
        /// Serializes every tool invocation end-to-end. The MCP HTTP transport can dispatch
        /// tool calls on arbitrary thread-pool threads and, depending on the client, may not
        /// wait for one call's response before issuing the next; without this lock a call like
        /// SwitchTraceSession could still be queued on the WPF dispatcher when a subsequent
        /// GetStatus call reads state, observing the previous session. Held for the full
        /// duration of each tool (including its dispatcher round-trip), not just the dispatcher
        /// hop, so calls are strictly ordered as if single-threaded.
        /// </summary>
        private static readonly SemaphoreSlim ToolLock = new(1, 1);

        private static MainWindow? GetMainWindow()
            => Application.Current?.Dispatcher.Invoke(() => Application.Current.MainWindow as MainWindow);

        [McpServerTool, Description("Returns basic info about the currently loaded HTTP trace file (path and row count), or a message if none is loaded.")]
        public static string GetTraceInfo()
        {
            ToolLock.Wait();
            try
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
            finally
            {
                ToolLock.Release();
            }
        }

        [McpServerTool, Description("Loads an HTTP trace file (.saz, .har, .etl, .trace, .log, or .txt) from disk as a new session. By default replaces the currently shown trace (matching prior behavior); pass sessionId/label and activate=false to load additional traces in the background for later comparison (see ListTraceSessions, SwitchTraceSession, DiffTraces). Sessions loaded this way start with no filter or highlight rules (rather than the UI's saved defaults) since the client is expected to configure those explicitly via FilterTrace/HighlightTrace. Returns a summary on success or an error message on failure.")]
        public static async Task<string> LoadTraceFile(
            [Description("Full path to the trace file to load.")] string path,
            [Description("Optional session id to register the trace under (e.g. 'good', 'bad'). Auto-generated (trace1, trace2, ...) when omitted. Must be unique among currently loaded sessions.")] string? sessionId = null,
            [Description("Optional display label for the session. Defaults to the file name.")] string? label = null,
            [Description("If true (default), immediately shows this trace in the analyser window. If false, loads it into the background session registry without changing what's currently shown.")] bool activate = true)
        {
            await ToolLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var window = GetMainWindow();
                if (window is null)
                    return "No HttpTraceAnalyser window is available.";

                if (string.IsNullOrWhiteSpace(path))
                    return "path must not be empty.";

                if (!File.Exists(path))
                    return $"File not found: {path}";

                var error = await window.Dispatcher.InvokeAsync(() => window.LoadTraceFileAsync(path, sessionId, label, activate, loadDefaultRules: false)).Task.Unwrap().ConfigureAwait(false);
                if (error is not null)
                    return $"Failed to load trace file '{path}': {error}";

                return window.Dispatcher.Invoke(() =>
                {
                    var session = TraceSessionManager.List().LastOrDefault(s => s.Trace.FilePath == path);
                    if (session is null)
                        return $"Loaded '{path}' but no trace data is available.";

                    return activate
                        ? $"Loaded and showing trace: session '{session.Id}' - {session.Trace.FilePath} ({session.Trace.Count} messages)."
                        : $"Loaded trace in background: session '{session.Id}' - {session.Trace.FilePath} ({session.Trace.Count} messages). Currently shown trace is unchanged.";
                });
            }
            finally
            {
                ToolLock.Release();
            }
        }

        [McpServerTool, Description("Searches a loaded HTTP trace for rows containing the given text (case-insensitive) and returns a summary of matching rows. scope='headers' searches URL/host/path/method/content-type/client-request-id/SOAP method/X-RequestId; scope='body' searches request and response payload text; scope='all' searches both (default).")]
        public static string SearchTrace(
            [Description("Text to search for.")] string searchText,
            [Description("Maximum number of matching rows to return.")] int maxResults = 20,
            [Description("Where to search: 'all' (default), 'headers' (URL/host/path/method/content-type/client-request-id/SOAP method/X-RequestId), or 'body' (request/response payload text).")] string scope = "all",
            [Description("Session id to search, as returned by ListTraceSessions or LoadTraceFile. Defaults to the currently shown trace when omitted.")] string? sessionId = null,
            [Description("Number of matches to skip before returning results (for paging through more than maxResults matches).")] int offset = 0,
            [Description("Redact known noisy auth query-string params (McasUserAuth, McasCtx, McasTsid) from URLs shown in output. Default true.")] bool stripQueryParams = true,
            [Description("Truncate each displayed URL to this many characters (0 = unlimited). Default 200.")] int maxUrlLength = 200)
        {
            ToolLock.Wait();
            try
            {
            var window = GetMainWindow();
            if (window is null)
                return "No HttpTraceAnalyser window is available.";

            return window.Dispatcher.Invoke(() =>
            {
                var (trace, error) = ResolveTrace(window, sessionId);
                if (trace is null)
                    return error!;

                if (string.IsNullOrWhiteSpace(searchText))
                    return "searchText must not be empty.";

                bool searchHeaders = scope is "all" or "headers";
                bool searchBody = scope is "all" or "body";
                if (!searchHeaders && !searchBody)
                    return "scope must be 'all', 'headers', or 'body'.";

                var allMatches = trace.Messages.AsEnumerable()
                    .Where(row =>
                        (searchHeaders && (
                            RowContains(row, TraceDataSchema.Url, searchText) ||
                            RowContains(row, TraceDataSchema.Host, searchText) ||
                            RowContains(row, TraceDataSchema.Path, searchText) ||
                            RowContains(row, TraceDataSchema.Method, searchText) ||
                            RowContains(row, TraceDataSchema.ContentType, searchText) ||
                            RowContains(row, TraceDataSchema.ClientRequestId, searchText) ||
                            RowContains(row, TraceDataSchema.SoapMethod, searchText) ||
                            RowContains(row, TraceDataSchema.XRequestId, searchText))) ||
                        (searchBody && PayloadContains(trace, row, searchText)))
                    .ToList();

                var matches = allMatches.Skip(offset).Take(maxResults)
                    .Select(row => $"#{row[TraceDataSchema.Index]} {row[TraceDataSchema.Method]} {RedactUrl(row[TraceDataSchema.Url] as string, stripQueryParams, maxUrlLength)} -> {(row[TraceDataSchema.Response] is int code ? code.ToString() : "(no response)")}")
                    .ToList();

                if (matches.Count == 0)
                    return $"No rows matched '{searchText}' (scope: {scope}).";

                return $"{allMatches.Count} total match(es); showing {matches.Count} (offset {offset}):{Environment.NewLine}{string.Join(Environment.NewLine, matches)}";
            });
            }
            finally
            {
                ToolLock.Release();
            }
        }

        private static bool PayloadContains(HttpTraceFile trace, DataRow row, string searchText)
        {
            var request = trace.GetRequest(row);
            if (request.Payload is { Length: > 0 } &&
                MainWindow.DecodePayloadText(request.Payload, request.Headers).Contains(searchText, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var response = trace.GetResponse(row);
            if (response?.Payload is { Length: > 0 } &&
                MainWindow.DecodePayloadText(response.Payload, response.Headers).Contains(searchText, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static bool RowContains(DataRow row, string column, string searchText)
            => row[column] is string s && s.Contains(searchText, StringComparison.OrdinalIgnoreCase);

        [McpServerTool, Description("Adds a highlight rule that colors matching rows in the trace grid. Column values: Index, Response, ReasonPhrase, Method, Host, Path, Url, Date, Time, Latency, ContentType, ClientRequestId, SoapMethod, XRequestId. Operator values: Equals, NotEquals, Contains, StartsWith, Regex, Range (value formatted as 'min-max').")]
        public static string HighlightTrace(
            [Description("Column to match against.")] HighlightColumn column,
            [Description("Comparison operator.")] HighlightOperator @operator,
            [Description("Value to compare, or 'min-max' when operator is Range.")] string value,
            [Description("Background color as a hex string, e.g. #FFFF00.")] string backgroundColorHex = "#FFFF00",
            [Description("Optional foreground (text) color as a hex string.")] string? foregroundColorHex = null)
        {
            ToolLock.Wait();
            try
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
                // Insert at the top so newly added rules take precedence over existing ones
                // (matching are evaluated top-to-bottom, first match wins). Otherwise a new
                // rule could be silently shadowed by an earlier rule (e.g. a default
                // response-range rule) that also matches the same row.
                HighlightRuleSet.Rules.Insert(0, rule);
                return $"Added highlight rule: {column} {@operator} '{value}' (background {backgroundColorHex}).";
            });
            }
            finally
            {
                ToolLock.Release();
            }
        }

        [McpServerTool, Description("Removes all highlight rules currently applied to the trace grid.")]
        public static string ClearHighlights()
        {
            ToolLock.Wait();
            try
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
            finally
            {
                ToolLock.Release();
            }
        }

        [McpServerTool, Description("Adds a filter rule restricting which rows are visible in the trace grid. Field values: Index, Response, ReasonPhrase, Method, Host, Path, Url, Date, Time, Latency, ContentType, ClientRequestId, SoapMethod, XRequestId. Comparator values: Equals, NotEquals, Contains, StartsWith, Range (value formatted as 'min-max'). Combinator (And/Or) determines how this rule combines with previously added rules.")]
        public static string FilterTrace(
            [Description("Field to match against.")] FilterField field,
            [Description("Comparison operator.")] FilterComparator comparator,
            [Description("Value to compare, or 'min-max' when comparator is Range.")] string value,
            [Description("Logical combinator with previously added rules.")] FilterCombinator combinator = FilterCombinator.And)
        {
            ToolLock.Wait();
            try
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
            finally
            {
                ToolLock.Release();
            }
        }

        [McpServerTool, Description("Removes all active filter rules, showing every row in the trace grid again.")]
        public static string ClearFilters()
        {
            ToolLock.Wait();
            try
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
            finally
            {
                ToolLock.Release();
            }
        }

        [McpServerTool, Description("Selects the trace row with the given index (as shown in the grid's Index column) so it is shown in the request/response viewers. Fails if the row is hidden by an active filter; use ClearFilters or FindAndSelectTraceRow first if needed.")]
        public static string SelectTraceRow(
            [Description("The Index column value of the row to select.")] int index)
        {
            ToolLock.Wait();
            try
            {
                var window = GetMainWindow();
                if (window is null)
                    return "No HttpTraceAnalyser window is available.";

                return window.Dispatcher.Invoke(() => window.SelectTraceRow(index));
            }
            finally
            {
                ToolLock.Release();
            }
        }

        [McpServerTool, Description("Finds the first row across the whole loaded trace (ignoring any active filter) matching the given field/comparator/value, selects it, and shows it in the request/response viewers. Field values: Index, Response, ReasonPhrase, Method, Host, Path, Url, Date, Time, Latency, ContentType, ClientRequestId, SoapMethod, XRequestId. Comparator values: Equals, NotEquals, Contains, StartsWith, Range (value formatted as 'min-max'). Useful for e.g. jumping straight to the first row with a specific error status code.")]
        public static string FindAndSelectTraceRow(
            [Description("Field to match against.")] FilterField field,
            [Description("Comparison operator.")] FilterComparator comparator,
            [Description("Value to compare, or 'min-max' when comparator is Range.")] string value)
        {
            ToolLock.Wait();
            try
            {
                var window = GetMainWindow();
                if (window is null)
                    return "No HttpTraceAnalyser window is available.";

                return window.Dispatcher.Invoke(() => window.FindAndSelectTraceRow(field, comparator, value));
            }
            finally
            {
                ToolLock.Release();
            }
        }

        [McpServerTool, Description("Returns full details for the trace row with the given index (as shown in the grid's Index column): request/response headers, decoded request/response bodies, status code/reason, and URL. Returns JSON. Body text is truncated to maxBodyLength characters (default 4000; use a larger value or 0 for unlimited if you need the whole body).")]
        public static string GetTraceRow(
            [Description("The Index column value of the row to retrieve.")] int index,
            [Description("Maximum characters of decoded body text to include per body (0 = unlimited).")] int maxBodyLength = 4000,
            [Description("Session id to read from, as returned by ListTraceSessions or LoadTraceFile. Defaults to the currently shown trace when omitted.")] string? sessionId = null,
            [Description("Redact known noisy auth query-string params (McasUserAuth, McasCtx, McasTsid) from the returned URL. Default true.")] bool stripQueryParams = true,
            [Description("Truncate the returned URL to this many characters (0 = unlimited). Default 200.")] int maxUrlLength = 200)
        {
            ToolLock.Wait();
            try
            {
            var window = GetMainWindow();
            if (window is null)
                return "No HttpTraceAnalyser window is available.";

            return window.Dispatcher.Invoke(() =>
            {
                var (trace, error) = ResolveTrace(window, sessionId);
                if (trace is null)
                    return error!;

                var row = trace.Messages.Rows.Find(index);
                if (row is null)
                    return $"Row #{index} was not found.";

                var request = trace.GetRequest(row);
                var response = trace.GetResponse(row);

                var result = new
                {
                    Index = index,
                    Method = request.Method,
                    Url = RedactUrl(request.Url?.ToString(), stripQueryParams, maxUrlLength),
                    StatusCode = response?.StatusCode,
                    ReasonPhrase = response?.ReasonPhrase,
                    RequestHeaders = request.Headers.Select(h => $"{h.Key}: {h.Value}").ToArray(),
                    RequestBody = Truncate(MainWindow.DecodePayloadText(request.Payload, request.Headers), maxBodyLength),
                    ResponseHeaders = response?.Headers.Select(h => $"{h.Key}: {h.Value}").ToArray() ?? Array.Empty<string>(),
                    ResponseBody = response is null
                        ? null
                        : Truncate(MainWindow.DecodePayloadText(response.Payload, response.Headers), maxBodyLength),
                };

                return JsonSerializer.Serialize(result, JsonOptions);
            });
            }
            finally
            {
                ToolLock.Release();
            }
        }

        [McpServerTool, Description("Returns rows from a loaded trace, optionally restricted by a filter query and/or a specific set of columns, with paging via limit/offset. Column values: Index, Response, ReasonPhrase, Method, Host, Path, Url, Date, Time, Latency, ContentType, ClientRequestId, SoapMethod, XRequestId. format controls the output shape: 'json' (default), 'table' (fixed-width text), 'csv', or 'markdown'.")]
        public static string GetRows(
            [Description("Field to filter on, or null to return all rows.")] FilterField? filterField = null,
            [Description("Comparator to use when filterField is set.")] FilterComparator filterComparator = FilterComparator.Equals,
            [Description("Value to compare, or 'min-max' when filterComparator is Range. Required when filterField is set.")] string? filterValue = null,
            [Description("Comma-separated column names to include (default: Index, Method, Url, Response, ReasonPhrase, Latency).")] string? columns = null,
            [Description("Maximum number of rows to return.")] int limit = 50,
            [Description("Number of matching rows to skip before returning results.")] int offset = 0,
            [Description("Session id to read from, as returned by ListTraceSessions or LoadTraceFile. Defaults to the currently shown trace when omitted.")] string? sessionId = null,
            [Description("Redact known noisy auth query-string params (McasUserAuth, McasCtx, McasTsid) from any Url column values. Default true.")] bool stripQueryParams = true,
            [Description("Truncate any Url column values to this many characters (0 = unlimited). Default 200.")] int maxUrlLength = 200,
            [Description("Output shape: 'json' (default, includes TotalMatches/Offset/Returned metadata), 'table', 'csv', or 'markdown' (the latter three return just the row data, with match counts as a leading comment line).")] string format = "json")
        {
            ToolLock.Wait();
            try
            {
            var window = GetMainWindow();
            if (window is null)
                return "No HttpTraceAnalyser window is available.";

            return window.Dispatcher.Invoke(() =>
            {
                var (trace, error) = ResolveTrace(window, sessionId);
                if (trace is null)
                    return error!;

                DataRow[] rows;
                if (filterField is { } field)
                {
                    if (string.IsNullOrEmpty(filterValue))
                        return "filterValue must be provided when filterField is set.";

                    var rule = new FilterRule { Field = field, Comparator = filterComparator, Value = filterValue };
                    var expr = rule.BuildExpression();
                    if (string.IsNullOrEmpty(expr))
                        return "Could not build a query for the given criteria (check the value format, e.g. 'min-max' for Range).";

                    try
                    {
                        rows = trace.Messages.Select(expr, $"[{TraceDataSchema.Index}] ASC");
                    }
                    catch (Exception ex) when (ex is EvaluateException or SyntaxErrorException or InvalidExpressionException)
                    {
                        return $"Query failed: {ex.Message}";
                    }
                }
                else
                {
                    rows = trace.Messages.Select(null, $"[{TraceDataSchema.Index}] ASC");
                }

                var columnNames = string.IsNullOrWhiteSpace(columns)
                    ? new[] { TraceDataSchema.Index, TraceDataSchema.Method, TraceDataSchema.Url, TraceDataSchema.Response, TraceDataSchema.ReasonPhrase, TraceDataSchema.Latency }
                    : columns.Split(',').Select(c => c.Trim()).Where(c => c.Length > 0).ToArray();

                foreach (var name in columnNames)
                {
                    if (!trace.Messages.Columns.Contains(name))
                        return $"Unknown column '{name}'.";
                }

                object? CellValue(DataRow row, string column)
                {
                    var value = row[column] is DBNull ? null : row[column];
                    if (string.Equals(column, TraceDataSchema.Url, StringComparison.OrdinalIgnoreCase) && value is string url)
                        return RedactUrl(url, stripQueryParams, maxUrlLength);
                    return value;
                }

                var page = rows.Skip(offset).Take(limit)
                    .Select(row => (IReadOnlyDictionary<string, object?>)columnNames.ToDictionary(c => c, c => CellValue(row, c)))
                    .ToList();

                if (format is "csv" or "markdown" or "table")
                {
                    var header = $"# {rows.Length} total match(es); showing {page.Count} (offset {offset})";
                    return header + Environment.NewLine + RenderTabular(columnNames, page, format);
                }

                var result = new
                {
                    TotalMatches = rows.Length,
                    Offset = offset,
                    Returned = page.Count,
                    Rows = page,
                };

                return JsonSerializer.Serialize(result, JsonOptions);
            });
            }
            finally
            {
                ToolLock.Release();
            }
        }

        [McpServerTool, Description("Returns a snapshot of the analyser's current state: loaded trace path/row count, the number of rows currently visible under the active filter, the active filter rules, and the active highlight rules. Use this to confirm the effect of FilterTrace/ClearFilters/HighlightTrace/ClearHighlights without guessing.")]
        public static string GetStatus()
        {
            ToolLock.Wait();
            try
            {
            var window = GetMainWindow();
            if (window is null)
                return "No HttpTraceAnalyser window is available.";

            return window.Dispatcher.Invoke(() =>
            {
                var trace = window.Trace;
                if (trace is null)
                {
                    return JsonSerializer.Serialize(new
                    {
                        TraceLoaded = false,
                        FilterRules = FilterRuleSet.Rules.Select(DescribeFilterRule).ToArray(),
                        HighlightRules = HighlightRuleSet.Rules.Select(DescribeHighlightRule).ToArray(),
                    }, JsonOptions);
                }

                var rowFilter = FilterRuleSet.BuildRowFilter();
                int visibleCount;
                try
                {
                    visibleCount = string.IsNullOrEmpty(rowFilter)
                        ? trace.Count
                        : trace.Messages.Select(rowFilter).Length;
                }
                catch (Exception ex) when (ex is EvaluateException or SyntaxErrorException or InvalidExpressionException)
                {
                    visibleCount = -1;
                }

                var result = new
                {
                    TraceLoaded = true,
                    trace.FilePath,
                    TotalRows = trace.Count,
                    VisibleRows = visibleCount,
                    FilterRules = FilterRuleSet.Rules.Select(DescribeFilterRule).ToArray(),
                    HighlightRules = HighlightRuleSet.Rules.Select(DescribeHighlightRule).ToArray(),
                };

                return JsonSerializer.Serialize(result, JsonOptions);
            });
            }
            finally
            {
                ToolLock.Release();
            }
        }

        private static object DescribeFilterRule(FilterRule rule) => new
        {
            rule.Combinator,
            Field = rule.ColumnName,
            rule.Comparator,
            rule.Value,
        };

        private static object DescribeHighlightRule(HighlightRule rule) => new
        {
            rule.IsEnabled,
            Column = rule.ColumnName,
            rule.Operator,
            rule.Value,
            BackgroundColor = rule.BackgroundColor.ToString(),
            ForegroundColor = rule.ForegroundColor?.ToString(),
        };

        [McpServerTool, Description("Groups a loaded trace's rows by one or two fields and returns counts per group, sorted by count descending. Useful for spotting patterns (e.g. group by Response to see status code distribution, or by Host,Path to find hot endpoints) without manually scanning every row. Field values: Index, Response, ReasonPhrase, Method, Host, Path, Url, Date, Time, Latency, ContentType, ClientRequestId, SoapMethod, XRequestId. format controls output shape: 'json' (default), 'table', 'csv', or 'markdown'.")]
        public static string AggregateTrace(
            [Description("Primary field to group by.")] FilterField groupBy,
            [Description("Optional secondary field to group by (e.g. group by Host then Path).")] FilterField? thenBy = null,
            [Description("Maximum number of groups to return.")] int maxGroups = 30,
            [Description("Session id to read from, as returned by ListTraceSessions or LoadTraceFile. Defaults to the currently shown trace when omitted.")] string? sessionId = null,
            [Description("Output shape: 'json' (default, includes GroupBy/TotalGroups metadata), 'table', 'csv', or 'markdown' (the latter three return just the Key/Count rows).")] string format = "json")
        {
            ToolLock.Wait();
            try
            {
            var window = GetMainWindow();
            if (window is null)
                return "No HttpTraceAnalyser window is available.";

            return window.Dispatcher.Invoke(() =>
            {
                var (trace, error) = ResolveTrace(window, sessionId);
                if (trace is null)
                    return error!;

                var primaryColumn = FieldToColumn(groupBy);
                var secondaryColumn = thenBy is { } t ? FieldToColumn(t) : null;

                var groups = trace.Messages.AsEnumerable()
                    .GroupBy(row => secondaryColumn is null
                        ? FormatCell(row[primaryColumn])
                        : $"{FormatCell(row[primaryColumn])} | {FormatCell(row[secondaryColumn])}")
                    .Select(g => new { Key = g.Key, Count = g.Count() })
                    .OrderByDescending(g => g.Count)
                    .Take(maxGroups)
                    .ToList();

                var groupByLabel = secondaryColumn is null ? groupBy.ToString() : $"{groupBy}, {thenBy}";

                if (format is "csv" or "markdown" or "table")
                {
                    var columns = new[] { "Key", "Count" };
                    var rows = groups
                        .Select(g => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["Key"] = g.Key, ["Count"] = g.Count })
                        .ToList();
                    var header = $"# GroupBy={groupByLabel}; {groups.Count} group(s)";
                    return header + Environment.NewLine + RenderTabular(columns, rows, format);
                }

                var result = new
                {
                    GroupBy = groupByLabel,
                    TotalGroups = groups.Count,
                    Groups = groups,
                };

                return JsonSerializer.Serialize(result, JsonOptions);
            });
            }
            finally
            {
                ToolLock.Release();
            }
        }

        private static string FieldToColumn(FilterField field) => field switch
        {
            FilterField.Response => TraceDataSchema.Response,
            FilterField.Method => TraceDataSchema.Method,
            FilterField.Host => TraceDataSchema.Host,
            FilterField.Path => TraceDataSchema.Path,
            FilterField.Url => TraceDataSchema.Url,
            FilterField.Date => TraceDataSchema.Date,
            FilterField.Time => TraceDataSchema.Time,
            FilterField.Index => TraceDataSchema.Index,
            FilterField.ReasonPhrase => TraceDataSchema.ReasonPhrase,
            FilterField.Latency => TraceDataSchema.Latency,
            FilterField.Process => TraceDataSchema.Process,
            FilterField.ContentType => TraceDataSchema.ContentType,
            FilterField.ClientRequestId => TraceDataSchema.ClientRequestId,
            FilterField.SoapMethod => TraceDataSchema.SoapMethod,
            FilterField.XRequestId => TraceDataSchema.XRequestId,
            _ => TraceDataSchema.Index,
        };

        private static string FormatCell(object? value)
            => value is null or DBNull ? "(none)" : value.ToString() ?? "(none)";

        private static string? Truncate(string? text, int maxLength)
        {
            if (text is null || maxLength <= 0 || text.Length <= maxLength)
                return text;
            return text[..maxLength] + $"... [truncated, {text.Length} total characters]";
        }

        [McpServerTool, Description("Lists all currently loaded trace sessions (the active/shown one plus any loaded in the background via LoadTraceFile with activate=false), with each session's id, label, file path, row count, load time, and whether it is the one currently shown in the analyser window.")]
        public static string ListTraceSessions()
        {
            ToolLock.Wait();
            try
            {
                var window = GetMainWindow();
                if (window is null)
                    return "No HttpTraceAnalyser window is available.";

                return window.Dispatcher.Invoke(() =>
                {
                    var activeId = TraceSessionManager.ActiveSessionId;
                    var sessions = TraceSessionManager.List()
                        .Select(s => new
                        {
                            s.Id,
                            s.Label,
                            Path = s.Trace.FilePath,
                            RowCount = s.Trace.Count,
                            LoadedAt = s.LoadedAt,
                            IsActive = string.Equals(s.Id, activeId, StringComparison.OrdinalIgnoreCase),
                        })
                        .ToList();

                    return JsonSerializer.Serialize(new { ActiveSessionId = activeId, Sessions = sessions }, JsonOptions);
                });
            }
            finally
            {
                ToolLock.Release();
            }
        }

        [McpServerTool, Description("Switches which loaded trace session is shown in the analyser window's grid and viewers (no disk I/O - the session must already be loaded via LoadTraceFile). Active/highlight filter rules continue to apply, now against the newly shown session's data.")]
        public static string SwitchTraceSession(
            [Description("The session id to show, as returned by ListTraceSessions or LoadTraceFile.")] string sessionId)
        {
            ToolLock.Wait();
            try
            {
                var window = GetMainWindow();
                if (window is null)
                    return "No HttpTraceAnalyser window is available.";

                if (string.IsNullOrWhiteSpace(sessionId))
                    return "sessionId must not be empty.";

                return window.Dispatcher.Invoke(() => window.SwitchToSession(sessionId));
            }
            finally
            {
                ToolLock.Release();
            }
        }

        [McpServerTool, Description("Closes (unloads) a trace session, freeing its memory. If it was the currently shown session, automatically switches to another loaded session if one exists, otherwise clears the viewer to the empty state.")]
        public static string CloseTraceSession(
            [Description("The session id to close, as returned by ListTraceSessions or LoadTraceFile.")] string sessionId)
        {
            ToolLock.Wait();
            try
            {
                var window = GetMainWindow();
                if (window is null)
                    return "No HttpTraceAnalyser window is available.";

                if (string.IsNullOrWhiteSpace(sessionId))
                    return "sessionId must not be empty.";

                return window.Dispatcher.Invoke(() => window.CloseSession(sessionId));
            }
            finally
            {
                ToolLock.Release();
            }
        }

        /// <summary>
        /// Resolves the <see cref="HttpTraceFile"/> to query for tools that accept an optional
        /// sessionId: the specified session when given, otherwise the currently active/shown one.
        /// Must be called on the UI dispatcher thread.
        /// </summary>
        private static (HttpTraceFile? Trace, string? Error) ResolveTrace(MainWindow window, string? sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                var trace = window.Trace;
                return trace is null
                    ? (null, "No trace file is currently loaded.")
                    : (trace, null);
            }

            var session = TraceSessionManager.Get(sessionId);
            return session is null
                ? (null, $"Session '{sessionId}' was not found. Use ListTraceSessions to see loaded sessions.")
                : (session.Trace, null);
        }

        [McpServerTool, Description("Compares two loaded trace sessions and reports differences: rows present only in one side, and for correlated rows (matched by ClientRequestId or X-RequestId when available, otherwise by matching Path in encounter order), differences in status code, latency, and response body length. Useful for answering 'what differs between a working and a failing trace'. detail='summary' returns only counts and per-pair status/latency (no URLs/full row data) for a quick first look; detail='full' (default) includes OnlyInA/OnlyInB row listings and URLs. format controls output shape: 'json' (default), 'table', 'csv', or 'markdown' (the latter three render the Differences list only).")]
        public static string DiffTraces(
            [Description("Session id of the first ('A'/baseline) trace, as returned by ListTraceSessions or LoadTraceFile.")] string sessionA,
            [Description("Session id of the second ('B'/comparison) trace.")] string sessionB,
            [Description("Maximum number of unmatched/differing rows to list per category.")] int maxResults = 20,
            [Description("Comma-separated column names to include in OnlyInA/OnlyInB row listings (default: Index, Method, Url, Response). Ignored in summary detail mode.")] string? columns = null,
            [Description("'full' (default): include OnlyInA/OnlyInB row listings and URLs. 'summary': counts plus per-pair CorrelatedBy/IndexA/IndexB/StatusA/StatusB/LatencyMsA/LatencyMsB only, omitting URLs and row listings, for a quick first look.")] string detail = "full",
            [Description("Redact known noisy auth query-string params (McasUserAuth, McasCtx, McasTsid) from any Url values. Default true.")] bool stripQueryParams = true,
            [Description("Truncate any Url values to this many characters (0 = unlimited). Default 200.")] int maxUrlLength = 200,
            [Description("Output shape: 'json' (default), 'table', 'csv', or 'markdown' (the latter three render just the Differences list).")] string format = "json")
        {
            ToolLock.Wait();
            try
            {
            var window = GetMainWindow();
            if (window is null)
                return "No HttpTraceAnalyser window is available.";

            if (detail is not ("full" or "summary"))
                return "detail must be 'full' or 'summary'.";

            return window.Dispatcher.Invoke(() =>
            {
                var a = TraceSessionManager.Get(sessionA);
                if (a is null)
                    return $"Session '{sessionA}' was not found.";
                var b = TraceSessionManager.Get(sessionB);
                if (b is null)
                    return $"Session '{sessionB}' was not found.";

                var columnNames = string.IsNullOrWhiteSpace(columns)
                    ? new[] { TraceDataSchema.Index, TraceDataSchema.Method, TraceDataSchema.Url, TraceDataSchema.Response }
                    : columns.Split(',').Select(c => c.Trim()).Where(c => c.Length > 0).ToArray();

                if (detail == "full")
                {
                    foreach (var name in columnNames)
                    {
                        if (!a.Trace.Messages.Columns.Contains(name) || !b.Trace.Messages.Columns.Contains(name))
                            return $"Unknown column '{name}'.";
                    }
                }

                var rowsA = a.Trace.Messages.AsEnumerable().ToList();
                var rowsB = b.Trace.Messages.AsEnumerable().ToList();

                string? CorrelationKey(DataRow row)
                {
                    var clientRequestId = row[TraceDataSchema.ClientRequestId] as string;
                    if (!string.IsNullOrEmpty(clientRequestId))
                        return $"crid:{clientRequestId}";
                    var xRequestId = row[TraceDataSchema.XRequestId] as string;
                    return !string.IsNullOrEmpty(xRequestId) ? $"xrid:{xRequestId}" : null;
                }

                var keyedA = new Dictionary<string, DataRow>(StringComparer.OrdinalIgnoreCase);
                var unkeyedA = new List<DataRow>();
                foreach (var row in rowsA)
                {
                    var key = CorrelationKey(row);
                    if (key is not null && !keyedA.ContainsKey(key))
                        keyedA[key] = row;
                    else
                        unkeyedA.Add(row);
                }

                var keyedB = new Dictionary<string, DataRow>(StringComparer.OrdinalIgnoreCase);
                var unkeyedB = new List<DataRow>();
                foreach (var row in rowsB)
                {
                    var key = CorrelationKey(row);
                    if (key is not null && !keyedB.ContainsKey(key))
                        keyedB[key] = row;
                    else
                        unkeyedB.Add(row);
                }

                var matchedPairs = new List<(DataRow RowA, DataRow RowB, string CorrelatedBy)>();
                var onlyInA = new List<DataRow>();
                var onlyInB = new List<DataRow>();

                foreach (var (key, rowA) in keyedA)
                {
                    if (keyedB.TryGetValue(key, out var rowB))
                    {
                        matchedPairs.Add((rowA, rowB, key.Split(':')[0]));
                        keyedB.Remove(key);
                    }
                    else
                    {
                        onlyInA.Add(rowA);
                    }
                }
                onlyInB.AddRange(keyedB.Values);

                // Fallback correlation for rows without a client/X-request id: match by Path in encounter order.
                var unkeyedBByPath = unkeyedB
                    .GroupBy(r => r[TraceDataSchema.Path] as string ?? string.Empty)
                    .ToDictionary(g => g.Key, g => new Queue<DataRow>(g));

                foreach (var rowA in unkeyedA)
                {
                    var path = rowA[TraceDataSchema.Path] as string ?? string.Empty;
                    if (unkeyedBByPath.TryGetValue(path, out var queue) && queue.Count > 0)
                        matchedPairs.Add((rowA, queue.Dequeue(), "path-order"));
                    else
                        onlyInA.Add(rowA);
                }
                onlyInB.AddRange(unkeyedBByPath.Values.SelectMany(q => q));

                IReadOnlyDictionary<string, object?> DescribeRow(DataRow row) => columnNames.ToDictionary(
                    c => c,
                    c => string.Equals(c, TraceDataSchema.Url, StringComparison.OrdinalIgnoreCase)
                        ? RedactUrl(row[c] as string, stripQueryParams, maxUrlLength)
                        : (row[c] is DBNull ? null : row[c]));

                var differences = new List<object>();
                foreach (var (rowA, rowB, correlatedBy) in matchedPairs)
                {
                    int? statusA = rowA[TraceDataSchema.Response] is int ca ? ca : null;
                    int? statusB = rowB[TraceDataSchema.Response] is int cb ? cb : null;
                    double? latencyA = rowA[TraceDataSchema.Latency] is double la ? la : null;
                    double? latencyB = rowB[TraceDataSchema.Latency] is double lb ? lb : null;
                    var responseA = a.Trace.GetResponse(rowA);
                    var responseB = b.Trace.GetResponse(rowB);
                    int bodyLenA = responseA?.Payload.Length ?? 0;
                    int bodyLenB = responseB?.Payload.Length ?? 0;

                    if (statusA == statusB && bodyLenA == bodyLenB)
                        continue;

                    if (detail == "summary")
                    {
                        differences.Add(new
                        {
                            CorrelatedBy = correlatedBy,
                            IndexA = rowA[TraceDataSchema.Index],
                            IndexB = rowB[TraceDataSchema.Index],
                            StatusA = statusA,
                            StatusB = statusB,
                            LatencyMsA = latencyA,
                            LatencyMsB = latencyB,
                        });
                    }
                    else
                    {
                        differences.Add(new
                        {
                            CorrelatedBy = correlatedBy,
                            Url = RedactUrl(rowA[TraceDataSchema.Url] as string, stripQueryParams, maxUrlLength),
                            IndexA = rowA[TraceDataSchema.Index],
                            IndexB = rowB[TraceDataSchema.Index],
                            StatusA = statusA,
                            StatusB = statusB,
                            LatencyMsA = latencyA,
                            LatencyMsB = latencyB,
                            ResponseBodyLengthA = bodyLenA,
                            ResponseBodyLengthB = bodyLenB,
                        });
                    }
                }

                var limitedDifferences = differences.Take(maxResults).ToList();

                if (format is "csv" or "markdown" or "table")
                {
                    var diffColumns = detail == "summary"
                        ? new[] { "CorrelatedBy", "IndexA", "IndexB", "StatusA", "StatusB", "LatencyMsA", "LatencyMsB" }
                        : new[] { "CorrelatedBy", "Url", "IndexA", "IndexB", "StatusA", "StatusB", "LatencyMsA", "LatencyMsB", "ResponseBodyLengthA", "ResponseBodyLengthB" };
                    var diffRows = limitedDifferences
                        .Select(d => (IReadOnlyDictionary<string, object?>)diffColumns.ToDictionary(
                            c => c,
                            c => (object?)d.GetType().GetProperty(c)?.GetValue(d)))
                        .ToList();

                    var header = $"# MatchedPairs={matchedPairs.Count} OnlyInA={onlyInA.Count} OnlyInB={onlyInB.Count} DifferingPairs={differences.Count} (showing {diffRows.Count})";
                    return header + Environment.NewLine + RenderTabular(diffColumns, diffRows, format);
                }

                if (detail == "summary")
                {
                    var summaryResult = new
                    {
                        SessionA = new { a.Id, a.Label, RowCount = rowsA.Count },
                        SessionB = new { b.Id, b.Label, RowCount = rowsB.Count },
                        MatchedPairCount = matchedPairs.Count,
                        OnlyInACount = onlyInA.Count,
                        OnlyInBCount = onlyInB.Count,
                        DifferingPairCount = differences.Count,
                        Differences = limitedDifferences,
                    };
                    return JsonSerializer.Serialize(summaryResult, JsonOptions);
                }

                var result = new
                {
                    SessionA = new { a.Id, a.Label, RowCount = rowsA.Count },
                    SessionB = new { b.Id, b.Label, RowCount = rowsB.Count },
                    MatchedPairCount = matchedPairs.Count,
                    OnlyInACount = onlyInA.Count,
                    OnlyInBCount = onlyInB.Count,
                    DifferingPairCount = differences.Count,
                    OnlyInA = onlyInA.Take(maxResults).Select(DescribeRow).ToList(),
                    OnlyInB = onlyInB.Take(maxResults).Select(DescribeRow).ToList(),
                    Differences = limitedDifferences,
                };

                return JsonSerializer.Serialize(result, JsonOptions);
            });
            }
            finally
            {
                ToolLock.Release();
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        /// <summary>Query-string parameter names whose values are large opaque auth/session blobs that dominate output without adding diagnostic value.</summary>
        private static readonly string[] DefaultRedactedQueryParams = { "McasUserAuth", "McasCtx", "McasTsid" };

        /// <summary>
        /// Redacts known-noisy query-string parameter values (see <see cref="DefaultRedactedQueryParams"/>)
        /// and/or truncates the overall URL, to keep MCP tool output compact. Returns <paramref name="url"/>
        /// unchanged when both <paramref name="stripQueryParams"/> is false and <paramref name="maxUrlLength"/> is 0.
        /// </summary>
        private static string RedactUrl(string? url, bool stripQueryParams, int maxUrlLength)
        {
            if (string.IsNullOrEmpty(url))
                return url ?? string.Empty;

            var result = url;

            if (stripQueryParams)
            {
                var queryStart = result.IndexOf('?');
                if (queryStart >= 0 && queryStart < result.Length - 1)
                {
                    var basePart = result[..queryStart];
                    var queryPart = result[(queryStart + 1)..];
                    var fragment = string.Empty;
                    var fragmentStart = queryPart.IndexOf('#');
                    if (fragmentStart >= 0)
                    {
                        fragment = queryPart[fragmentStart..];
                        queryPart = queryPart[..fragmentStart];
                    }

                    var pairs = queryPart.Split('&', StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < pairs.Length; i++)
                    {
                        var eq = pairs[i].IndexOf('=');
                        if (eq < 0)
                            continue;
                        var key = pairs[i][..eq];
                        if (DefaultRedactedQueryParams.Any(p => string.Equals(p, key, StringComparison.OrdinalIgnoreCase)))
                            pairs[i] = $"{key}=<redacted>";
                    }

                    result = pairs.Length == 0 ? basePart : $"{basePart}?{string.Join('&', pairs)}{fragment}";
                }
            }

            if (maxUrlLength > 0 && result.Length > maxUrlLength)
                result = result[..maxUrlLength] + "...<truncated>";

            return result;
        }

        /// <summary>
        /// Renders tabular data (a fixed column list plus per-row values) as CSV, Markdown, or a
        /// fixed-width text table. Used by tools that support a <c>format</c> parameter as an
        /// alternative to raw JSON, to avoid requiring the caller to pretty-print JSON themselves.
        /// </summary>
        private static string RenderTabular(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string format)
        {
            string Cell(IReadOnlyDictionary<string, object?> row, string column)
                => row.TryGetValue(column, out var value) ? FormatCell(value) : string.Empty;

            switch (format)
            {
                case "csv":
                {
                    string CsvEscape(string s)
                        => s.Contains(',') || s.Contains('"') || s.Contains('\n')
                            ? "\"" + s.Replace("\"", "\"\"") + "\""
                            : s;

                    var sb = new StringBuilder();
                    sb.AppendLine(string.Join(',', columns.Select(CsvEscape)));
                    foreach (var row in rows)
                        sb.AppendLine(string.Join(',', columns.Select(c => CsvEscape(Cell(row, c)))));
                    return sb.ToString();
                }

                case "markdown":
                {
                    var sb = new StringBuilder();
                    sb.Append("| ").Append(string.Join(" | ", columns)).AppendLine(" |");
                    sb.Append("| ").Append(string.Join(" | ", columns.Select(_ => "---"))).AppendLine(" |");
                    foreach (var row in rows)
                        sb.Append("| ").Append(string.Join(" | ", columns.Select(c => Cell(row, c).Replace("|", "\\|")))).AppendLine(" |");
                    return sb.ToString();
                }

                case "table":
                default:
                {
                    var widths = columns.Select(c => Math.Max(c.Length, rows.Count == 0 ? 0 : rows.Max(r => Cell(r, c).Length))).ToArray();
                    var sb = new StringBuilder();

                    void AppendRow(IEnumerable<string> cells)
                    {
                        sb.AppendLine(string.Join("  ", cells.Select((cell, i) => cell.PadRight(widths[i]))));
                    }

                    AppendRow(columns);
                    AppendRow(widths.Select(w => new string('-', w)));
                    foreach (var row in rows)
                        AppendRow(columns.Select(c => Cell(row, c)));
                    return sb.ToString();
                }
            }
        }
    }
}
