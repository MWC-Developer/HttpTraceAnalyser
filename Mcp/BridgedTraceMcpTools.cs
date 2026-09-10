using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using HttpTraceAnalyser.Model;
using ModelContextProtocol.Server;

namespace HttpTraceAnalyser.Mcp
{
    /// <summary>
    /// MCP tool-type used only by the "--mcp-stdio" console-mode process (see
    /// <see cref="McpStdioHost"/>). Mirrors every tool exposed by <see cref="TraceMcpTools"/> with
    /// identical names/parameters/descriptions so MCP clients see the same schema regardless of
    /// how the server was launched, but forwards each call over the local named-pipe bridge (see
    /// <see cref="TraceBridgeClient"/>) to the already-running GUI process that owns the live,
    /// in-memory trace data, instead of executing the tool body directly.
    /// </summary>
    [McpServerToolType]
    public static class BridgedTraceMcpTools
    {
        private static readonly TraceBridgeClient Client = new(pipeNameSuffix: McpStdioHost.PipeNameSuffix);

        private static async Task<string> InvokeAsync(string tool, Dictionary<string, object?> args, CancellationToken cancellationToken)
        {
            try
            {
                var result = await Client.InvokeAsync(tool, args, cancellationToken).ConfigureAwait(false);
                return result ?? string.Empty;
            }
            catch (System.Exception ex)
            {
                return $"MCP bridge error: {ex.Message}";
            }
        }

        [McpServerTool, Description("Returns basic info about the currently loaded HTTP trace file (path and row count), or a message if none is loaded.")]
        public static Task<string> GetTraceInfo(CancellationToken cancellationToken = default)
            => InvokeAsync(nameof(GetTraceInfo), new(), cancellationToken);

        [McpServerTool, Description("Loads an HTTP trace file (.saz, .har, .etl, .trace, .log, or .txt) from disk as a new session. By default replaces the currently shown trace (matching prior behavior); pass sessionId/label and activate=false to load additional traces in the background for later comparison (see ListTraceSessions, SwitchTraceSession, DiffTraces). Sessions loaded this way start with no filter or highlight rules (rather than the UI's saved defaults) since the client is expected to configure those explicitly via FilterTrace/HighlightTrace. Returns a summary on success or an error message on failure.")]
        public static Task<string> LoadTraceFile(
            [Description("Full path to the trace file to load.")] string path,
            [Description("Optional session id to register the trace under (e.g. 'good', 'bad'). Auto-generated (trace1, trace2, ...) when omitted. Must be unique among currently loaded sessions.")] string? sessionId = null,
            [Description("Optional display label for the session. Defaults to the file name.")] string? label = null,
            [Description("If true (default), immediately shows this trace in the analyser window. If false, loads it into the background session registry without changing what's currently shown.")] bool activate = true,
            CancellationToken cancellationToken = default)
            => InvokeAsync(nameof(LoadTraceFile), new() { ["path"] = path, ["sessionId"] = sessionId, ["label"] = label, ["activate"] = activate }, cancellationToken);

        [McpServerTool, Description("Searches a loaded HTTP trace for rows containing the given text (case-insensitive) and returns a summary of matching rows. scope='headers' searches URL/host/path/method/content-type/client-request-id/SOAP method/X-RequestId; scope='body' searches request and response payload text; scope='all' searches both (default).")]
        public static Task<string> SearchTrace(
            [Description("Text to search for.")] string searchText,
            [Description("Maximum number of matching rows to return.")] int maxResults = 20,
            [Description("Where to search: 'all' (default), 'headers' (URL/host/path/method/content-type/client-request-id/SOAP method/X-RequestId), or 'body' (request/response payload text).")] string scope = "all",
            [Description("Session id to search, as returned by ListTraceSessions or LoadTraceFile. Defaults to the currently shown trace when omitted.")] string? sessionId = null,
            [Description("Number of matches to skip before returning results (for paging through more than maxResults matches).")] int offset = 0,
            [Description("Redact known noisy auth query-string params (McasUserAuth, McasCtx, McasTsid) from URLs shown in output. Default true.")] bool stripQueryParams = true,
            [Description("Truncate each displayed URL to this many characters (0 = unlimited). Default 200.")] int maxUrlLength = 200,
            CancellationToken cancellationToken = default)
            => InvokeAsync(nameof(SearchTrace), new()
            {
                ["searchText"] = searchText,
                ["maxResults"] = maxResults,
                ["scope"] = scope,
                ["sessionId"] = sessionId,
                ["offset"] = offset,
                ["stripQueryParams"] = stripQueryParams,
                ["maxUrlLength"] = maxUrlLength,
            }, cancellationToken);

        [McpServerTool, Description("Adds a highlight rule that colors matching rows in the trace grid. Column values: Index, Response, ReasonPhrase, Method, Host, Path, Url, Date, Time, Latency, ContentType, ClientRequestId, SoapMethod, XRequestId. Operator values: Equals, NotEquals, Contains, StartsWith, Regex, Range (value formatted as 'min-max').")]
        public static Task<string> HighlightTrace(
            [Description("Column to match against.")] HighlightColumn column,
            [Description("Comparison operator.")] HighlightOperator @operator,
            [Description("Value to compare, or 'min-max' when operator is Range.")] string value,
            [Description("Background color as a hex string, e.g. #FFFF00.")] string backgroundColorHex = "#FFFF00",
            [Description("Optional foreground (text) color as a hex string.")] string? foregroundColorHex = null,
            CancellationToken cancellationToken = default)
            => InvokeAsync(nameof(HighlightTrace), new()
            {
                ["column"] = column,
                ["operator"] = @operator,
                ["value"] = value,
                ["backgroundColorHex"] = backgroundColorHex,
                ["foregroundColorHex"] = foregroundColorHex,
            }, cancellationToken);

        [McpServerTool, Description("Removes all highlight rules currently applied to the trace grid.")]
        public static Task<string> ClearHighlights(CancellationToken cancellationToken = default)
            => InvokeAsync(nameof(ClearHighlights), new(), cancellationToken);

        [McpServerTool, Description("Adds a filter rule restricting which rows are visible in the trace grid. Field values: Index, Response, ReasonPhrase, Method, Host, Path, Url, Date, Time, Latency, ContentType, ClientRequestId, SoapMethod, XRequestId. Comparator values: Equals, NotEquals, Contains, StartsWith, Range (value formatted as 'min-max'). Combinator (And/Or) determines how this rule combines with previously added rules.")]
        public static Task<string> FilterTrace(
            [Description("Field to match against.")] FilterField field,
            [Description("Comparison operator.")] FilterComparator comparator,
            [Description("Value to compare, or 'min-max' when comparator is Range.")] string value,
            [Description("Logical combinator with previously added rules.")] FilterCombinator combinator = FilterCombinator.And,
            CancellationToken cancellationToken = default)
            => InvokeAsync(nameof(FilterTrace), new()
            {
                ["field"] = field,
                ["comparator"] = comparator,
                ["value"] = value,
                ["combinator"] = combinator,
            }, cancellationToken);

        [McpServerTool, Description("Removes all active filter rules, showing every row in the trace grid again.")]
        public static Task<string> ClearFilters(CancellationToken cancellationToken = default)
            => InvokeAsync(nameof(ClearFilters), new(), cancellationToken);

        [McpServerTool, Description("Selects the trace row with the given index (as shown in the grid's Index column) so it is shown in the request/response viewers. Fails if the row is hidden by an active filter; use ClearFilters or FindAndSelectTraceRow first if needed.")]
        public static Task<string> SelectTraceRow(
            [Description("The Index column value of the row to select.")] int index,
            CancellationToken cancellationToken = default)
            => InvokeAsync(nameof(SelectTraceRow), new() { ["index"] = index }, cancellationToken);

        [McpServerTool, Description("Finds the first row across the whole loaded trace (ignoring any active filter) matching the given field/comparator/value, selects it, and shows it in the request/response viewers. Field values: Index, Response, ReasonPhrase, Method, Host, Path, Url, Date, Time, Latency, ContentType, ClientRequestId, SoapMethod, XRequestId. Comparator values: Equals, NotEquals, Contains, StartsWith, Range (value formatted as 'min-max'). Useful for e.g. jumping straight to the first row with a specific error status code.")]
        public static Task<string> FindAndSelectTraceRow(
            [Description("Field to match against.")] FilterField field,
            [Description("Comparison operator.")] FilterComparator comparator,
            [Description("Value to compare, or 'min-max' when comparator is Range.")] string value,
            CancellationToken cancellationToken = default)
            => InvokeAsync(nameof(FindAndSelectTraceRow), new() { ["field"] = field, ["comparator"] = comparator, ["value"] = value }, cancellationToken);

        [McpServerTool, Description("Returns full details for the trace row with the given index (as shown in the grid's Index column): request/response headers, decoded request/response bodies, status code/reason, and URL. Returns JSON. Body text is truncated to maxBodyLength characters (default 4000; use a larger value or 0 for unlimited if you need the whole body).")]
        public static Task<string> GetTraceRow(
            [Description("The Index column value of the row to retrieve.")] int index,
            [Description("Maximum characters of decoded body text to include per body (0 = unlimited).")] int maxBodyLength = 4000,
            [Description("Session id to read from, as returned by ListTraceSessions or LoadTraceFile. Defaults to the currently shown trace when omitted.")] string? sessionId = null,
            [Description("Redact known noisy auth query-string params (McasUserAuth, McasCtx, McasTsid) from the returned URL. Default true.")] bool stripQueryParams = true,
            [Description("Truncate the returned URL to this many characters (0 = unlimited). Default 200.")] int maxUrlLength = 200,
            CancellationToken cancellationToken = default)
            => InvokeAsync(nameof(GetTraceRow), new()
            {
                ["index"] = index,
                ["maxBodyLength"] = maxBodyLength,
                ["sessionId"] = sessionId,
                ["stripQueryParams"] = stripQueryParams,
                ["maxUrlLength"] = maxUrlLength,
            }, cancellationToken);

        [McpServerTool, Description("Returns rows from a loaded trace, optionally restricted by a filter query and/or a specific set of columns, with paging via limit/offset. Column values: Index, Response, ReasonPhrase, Method, Host, Path, Url, Date, Time, Latency, ContentType, ClientRequestId, SoapMethod, XRequestId. format controls the output shape: 'json' (default), 'table' (fixed-width text), 'csv', or 'markdown'.")]
        public static Task<string> GetRows(
            [Description("Field to filter on, or null to return all rows.")] FilterField? filterField = null,
            [Description("Comparator to use when filterField is set.")] FilterComparator filterComparator = FilterComparator.Equals,
            [Description("Value to compare, or 'min-max' when filterComparator is Range. Required when filterField is set.")] string? filterValue = null,
            [Description("Comma-separated column names to include (default: Index, Method, Url, Response, ReasonPhrase, Latency).")] string? columns = null,
            [Description("Maximum number of rows to return.")] int limit = 50,
            [Description("Number of matching rows to skip before returning results.")] int offset = 0,
            [Description("Session id to read from, as returned by ListTraceSessions or LoadTraceFile. Defaults to the currently shown trace when omitted.")] string? sessionId = null,
            [Description("Redact known noisy auth query-string params (McasUserAuth, McasCtx, McasTsid) from any Url column values. Default true.")] bool stripQueryParams = true,
            [Description("Truncate any Url column values to this many characters (0 = unlimited). Default 200.")] int maxUrlLength = 200,
            [Description("Output shape: 'json' (default, includes TotalMatches/Offset/Returned metadata), 'table', 'csv', or 'markdown' (the latter three return just the row data, with match counts as a leading comment line).")] string format = "json",
            CancellationToken cancellationToken = default)
            => InvokeAsync(nameof(GetRows), new()
            {
                ["filterField"] = filterField,
                ["filterComparator"] = filterComparator,
                ["filterValue"] = filterValue,
                ["columns"] = columns,
                ["limit"] = limit,
                ["offset"] = offset,
                ["sessionId"] = sessionId,
                ["stripQueryParams"] = stripQueryParams,
                ["maxUrlLength"] = maxUrlLength,
                ["format"] = format,
            }, cancellationToken);

        [McpServerTool, Description("Returns a snapshot of the analyser's current state: loaded trace path/row count, the number of rows currently visible under the active filter, the active filter rules, and the active highlight rules. Use this to confirm the effect of FilterTrace/ClearFilters/HighlightTrace/ClearHighlights without guessing.")]
        public static Task<string> GetStatus(CancellationToken cancellationToken = default)
            => InvokeAsync(nameof(GetStatus), new(), cancellationToken);

        [McpServerTool, Description("Groups a loaded trace's rows by one or two fields and returns counts per group, sorted by count descending. Useful for spotting patterns (e.g. group by Response to see status code distribution, or by Host,Path to find hot endpoints) without manually scanning every row. Field values: Index, Response, ReasonPhrase, Method, Host, Path, Url, Date, Time, Latency, ContentType, ClientRequestId, SoapMethod, XRequestId. format controls output shape: 'json' (default), 'table', 'csv', or 'markdown'.")]
        public static Task<string> AggregateTrace(
            [Description("Primary field to group by.")] FilterField groupBy,
            [Description("Optional secondary field to group by (e.g. group by Host then Path).")] FilterField? thenBy = null,
            [Description("Maximum number of groups to return.")] int maxGroups = 30,
            [Description("Session id to read from, as returned by ListTraceSessions or LoadTraceFile. Defaults to the currently shown trace when omitted.")] string? sessionId = null,
            [Description("Output shape: 'json' (default, includes GroupBy/TotalGroups metadata), 'table', 'csv', or 'markdown' (the latter three return just the Key/Count rows).")] string format = "json",
            CancellationToken cancellationToken = default)
            => InvokeAsync(nameof(AggregateTrace), new()
            {
                ["groupBy"] = groupBy,
                ["thenBy"] = thenBy,
                ["maxGroups"] = maxGroups,
                ["sessionId"] = sessionId,
                ["format"] = format,
            }, cancellationToken);

        [McpServerTool, Description("Lists all currently loaded trace sessions (the active/shown one plus any loaded in the background via LoadTraceFile with activate=false), with each session's id, label, file path, row count, load time, and whether it is the one currently shown in the analyser window.")]
        public static Task<string> ListTraceSessions(CancellationToken cancellationToken = default)
            => InvokeAsync(nameof(ListTraceSessions), new(), cancellationToken);

        [McpServerTool, Description("Switches which loaded trace session is shown in the analyser window's grid and viewers (no disk I/O - the session must already be loaded via LoadTraceFile). Active/highlight filter rules continue to apply, now against the newly shown session's data.")]
        public static Task<string> SwitchTraceSession(
            [Description("The session id to show, as returned by ListTraceSessions or LoadTraceFile.")] string sessionId,
            CancellationToken cancellationToken = default)
            => InvokeAsync(nameof(SwitchTraceSession), new() { ["sessionId"] = sessionId }, cancellationToken);

        [McpServerTool, Description("Closes (unloads) a trace session, freeing its memory. If it was the currently shown session, automatically switches to another loaded session if one exists, otherwise clears the viewer to the empty state.")]
        public static Task<string> CloseTraceSession(
            [Description("The session id to close, as returned by ListTraceSessions or LoadTraceFile.")] string sessionId,
            CancellationToken cancellationToken = default)
            => InvokeAsync(nameof(CloseTraceSession), new() { ["sessionId"] = sessionId }, cancellationToken);

        [McpServerTool, Description("Compares two loaded trace sessions and reports differences: rows present only in one side, and for correlated rows (matched by ClientRequestId or X-RequestId when available, otherwise by matching Path in encounter order), differences in status code, latency, and response body length. Useful for answering 'what differs between a working and a failing trace'. detail='summary' returns only counts and per-pair status/latency (no URLs/full row data) for a quick first look; detail='full' (default) includes OnlyInA/OnlyInB row listings and URLs. format controls output shape: 'json' (default), 'table', 'csv', or 'markdown' (the latter three render the Differences list only).")]
        public static Task<string> DiffTraces(
            [Description("Session id of the first ('A'/baseline) trace, as returned by ListTraceSessions or LoadTraceFile.")] string sessionA,
            [Description("Session id of the second ('B'/comparison) trace.")] string sessionB,
            [Description("Maximum number of unmatched/differing rows to list per category.")] int maxResults = 20,
            [Description("Comma-separated column names to include in OnlyInA/OnlyInB row listings (default: Index, Method, Url, Response). Ignored in summary detail mode.")] string? columns = null,
            [Description("'full' (default): include OnlyInA/OnlyInB row listings and URLs. 'summary': counts plus per-pair CorrelatedBy/IndexA/IndexB/StatusA/StatusB/LatencyMsA/LatencyMsB only, omitting URLs and row listings, for a quick first look.")] string detail = "full",
            [Description("Redact known noisy auth query-string params (McasUserAuth, McasCtx, McasTsid) from any Url values. Default true.")] bool stripQueryParams = true,
            [Description("Truncate any Url values to this many characters (0 = unlimited). Default 200.")] int maxUrlLength = 200,
            [Description("Output shape: 'json' (default), 'table', 'csv', or 'markdown' (the latter three render just the Differences list).")] string format = "json",
            CancellationToken cancellationToken = default)
            => InvokeAsync(nameof(DiffTraces), new()
            {
                ["sessionA"] = sessionA,
                ["sessionB"] = sessionB,
                ["maxResults"] = maxResults,
                ["columns"] = columns,
                ["detail"] = detail,
                ["stripQueryParams"] = stripQueryParams,
                ["maxUrlLength"] = maxUrlLength,
                ["format"] = format,
            }, cancellationToken);
    }
}
