using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text;
using System.Windows.Media;

namespace HttpTraceAnalyser.Model
{
    /// <summary>
    /// Column names for the in-memory <see cref="DataTable"/> that backs a loaded trace.
    /// Column names intentionally match the properties that the ListView columns and
    /// <see cref="HighlightColumn"/> reference so that <see cref="DataRowView"/> can be
    /// used as the item source with the existing XAML bindings.
    /// </summary>
    internal static class TraceDataSchema
    {
        public const string Index = nameof(Index);
        public const string RequestTimestamp = nameof(RequestTimestamp);
        public const string ResponseTimestamp = nameof(ResponseTimestamp);
        public const string Date = nameof(Date);
        public const string Time = nameof(Time);
        public const string Method = nameof(Method);
        public const string Response = nameof(Response);
        public const string ReasonPhrase = nameof(ReasonPhrase);
        public const string Url = nameof(Url);
        public const string Host = nameof(Host);
        public const string Path = nameof(Path);
        public const string RequestHeaders = nameof(RequestHeaders);
        public const string ResponseHeaders = nameof(ResponseHeaders);
        public const string RequestPayload = nameof(RequestPayload);
        public const string ResponsePayload = nameof(ResponsePayload);
        public const string Latency = nameof(Latency);
        public const string RowBackground = nameof(RowBackground);
        public const string RowForeground = nameof(RowForeground);
    }

    /// <summary>
    /// Base class for a loaded HTTP trace file. Trace contents live in an in-memory
    /// <see cref="DataTable"/> ("Messages") so filtering, sorting, and grid virtualization
    /// scale to very large captures. Use <see cref="Load"/> to open a file; concrete
    /// subclasses handle specific formats (e.g. Fiddler .saz).
    /// </summary>
    public abstract class HttpTraceFile
    {
        private static readonly Dictionary<string, Func<string, HttpTraceFile>> Loaders =
            new(StringComparer.OrdinalIgnoreCase)
            {
                [".saz"] = path => new SazTraceFile(path),
                [".har"] = path => new HarTraceFile(path),
                [".etl"] = path => new EtlTraceFile(path),
            };

        // Delimiters used to serialize a header list into a single string cell.
        // Chosen to be characters that must not appear inside header names/values.
        private const char HeaderPairDelimiter = '\u001F';   // unit separator
        private const char HeaderRecordDelimiter = '\u001E'; // record separator

        private readonly DataTable _messages;

        protected HttpTraceFile(string filePath)
        {
            FilePath = filePath;
            _messages = CreateSchema();
        }

        public string FilePath { get; }

        /// <summary>
        /// Optional per-provider event count map, populated by loaders that see multiple
        /// ETW/event providers (currently the ETL loader). Null for formats without a
        /// provider concept. Used by the Summary viewer to describe what was found in
        /// the trace even when no HTTP messages could be extracted.
        /// </summary>
        public IReadOnlyDictionary<string, int>? ProviderEventCounts { get; protected set; }

        /// <summary>The in-memory table backing this trace. One row per request/response pair.</summary>
        public DataTable Messages => _messages;

        /// <summary>A <see cref="DataView"/> over <see cref="Messages"/>, suitable for grid binding.</summary>
        public DataView View => _messages.DefaultView;

        /// <summary>Total number of request/response pairs currently in the trace.</summary>
        public int Count => _messages.Rows.Count;

        private static DataTable CreateSchema()
        {
            var table = new DataTable("Messages");
            var cols = table.Columns;
            cols.Add(TraceDataSchema.Index, typeof(int));
            cols.Add(TraceDataSchema.RequestTimestamp, typeof(DateTime));
            cols.Add(TraceDataSchema.ResponseTimestamp, typeof(DateTime));
            cols.Add(TraceDataSchema.Date, typeof(string));
            cols.Add(TraceDataSchema.Time, typeof(string));
            cols.Add(TraceDataSchema.Method, typeof(string));
            cols.Add(TraceDataSchema.Response, typeof(int));
            cols.Add(TraceDataSchema.ReasonPhrase, typeof(string));
            cols.Add(TraceDataSchema.Url, typeof(string));
            cols.Add(TraceDataSchema.Host, typeof(string));
            cols.Add(TraceDataSchema.Path, typeof(string));
            cols.Add(TraceDataSchema.RequestHeaders, typeof(string));
            cols.Add(TraceDataSchema.ResponseHeaders, typeof(string));
            cols.Add(TraceDataSchema.RequestPayload, typeof(byte[]));
            cols.Add(TraceDataSchema.ResponsePayload, typeof(byte[]));
            cols.Add(TraceDataSchema.Latency, typeof(double));
            cols.Add(TraceDataSchema.RowBackground, typeof(Brush));
            cols.Add(TraceDataSchema.RowForeground, typeof(Brush));
            table.PrimaryKey = new[] { cols[TraceDataSchema.Index]! };
            return table;
        }

        /// <summary>Appends a request/response pair to the table.</summary>
        protected internal void AddRow(HttpRequest request, HttpResponse? response)
        {
            if (request is null)
                throw new ArgumentNullException(nameof(request));

            var row = _messages.NewRow();
            row[TraceDataSchema.Index] = _messages.Rows.Count;

            if (request.Timestamp is { } reqTs)
            {
                var local = reqTs.ToLocalTime();
                row[TraceDataSchema.RequestTimestamp] = local.DateTime;
                row[TraceDataSchema.Date] = local.ToString("yyyy-MM-dd");
                row[TraceDataSchema.Time] = local.ToString("HH:mm:ss.fff");
            }
            else
            {
                row[TraceDataSchema.Date] = string.Empty;
                row[TraceDataSchema.Time] = string.Empty;
            }

            row[TraceDataSchema.Method] = request.Method ?? string.Empty;
            row[TraceDataSchema.Url] = request.Url?.ToString() ?? string.Empty;
            row[TraceDataSchema.Host] = request.Host;
            row[TraceDataSchema.Path] = request.Path;
            row[TraceDataSchema.RequestHeaders] = SerializeHeaders(request.Headers);
            row[TraceDataSchema.RequestPayload] = request.Payload ?? Array.Empty<byte>();

            if (response is not null)
            {
                if (response.Timestamp is { } respTs)
                    row[TraceDataSchema.ResponseTimestamp] = respTs.ToLocalTime().DateTime;
                if (response.StatusCode is { } code)
                    row[TraceDataSchema.Response] = code;
                row[TraceDataSchema.ReasonPhrase] = response.ReasonPhrase ?? string.Empty;
                row[TraceDataSchema.ResponseHeaders] = SerializeHeaders(response.Headers);
                row[TraceDataSchema.ResponsePayload] = response.Payload ?? Array.Empty<byte>();

                if (request.Timestamp is { } reqTsForLatency && response.Timestamp is { } respTsForLatency)
                {
                    var latency = (respTsForLatency - reqTsForLatency).TotalMilliseconds;
                    if (latency >= 0)
                        row[TraceDataSchema.Latency] = latency;
                }
            }
            else
            {
                row[TraceDataSchema.ReasonPhrase] = string.Empty;
                row[TraceDataSchema.ResponseHeaders] = string.Empty;
                row[TraceDataSchema.ResponsePayload] = Array.Empty<byte>();
            }

            ApplyHighlight(row);
            _messages.Rows.Add(row);
        }

        /// <summary>Removes the row with the given <see cref="TraceDataSchema.Index"/> value.</summary>
        public bool RemoveByIndex(int index)
        {
            var row = _messages.Rows.Find(index);
            if (row is null)
                return false;
            _messages.Rows.Remove(row);
            return true;
        }

        /// <summary>Re-evaluates highlight rules against every row.</summary>
        public void RecomputeHighlights()
        {
            foreach (DataRow row in _messages.Rows)
            {
                if (row.RowState is DataRowState.Deleted or DataRowState.Detached)
                    continue;
                ApplyHighlight(row);
            }
        }

        private static void ApplyHighlight(DataRow row)
        {
            row[TraceDataSchema.RowBackground] = (object?)HighlightRuleSet.GetBackground(row) ?? DBNull.Value;
            row[TraceDataSchema.RowForeground] = (object?)HighlightRuleSet.GetForeground(row) ?? DBNull.Value;
        }

        /// <summary>Rebuilds an <see cref="HttpRequest"/> from the given row.</summary>
        public HttpRequest GetRequest(DataRow row)
        {
            if (row is null)
                throw new ArgumentNullException(nameof(row));

            var headers = DeserializeHeaders(row[TraceDataSchema.RequestHeaders] as string);
            var payload = row[TraceDataSchema.RequestPayload] as byte[] ?? Array.Empty<byte>();
            var method = row[TraceDataSchema.Method] as string ?? string.Empty;
            var urlText = row[TraceDataSchema.Url] as string ?? string.Empty;
            var timestamp = row[TraceDataSchema.RequestTimestamp] is DateTime ts
                ? new DateTimeOffset(DateTime.SpecifyKind(ts, DateTimeKind.Local))
                : (DateTimeOffset?)null;

            var url = Uri.TryCreate(urlText, UriKind.Absolute, out var abs)
                ? abs
                : new Uri(string.IsNullOrEmpty(urlText) ? "about:blank" : urlText, UriKind.RelativeOrAbsolute);

            return new HttpRequest(timestamp, headers, payload, method, url);
        }

        /// <summary>Rebuilds an <see cref="HttpResponse"/> from the given row (null if none was captured).</summary>
        public HttpResponse? GetResponse(DataRow row)
        {
            if (row is null)
                throw new ArgumentNullException(nameof(row));

            var hasResponse = row[TraceDataSchema.Response] is int
                || (row[TraceDataSchema.ResponsePayload] is byte[] payloadCheck && payloadCheck.Length > 0)
                || (row[TraceDataSchema.ResponseHeaders] is string headerCheck && headerCheck.Length > 0)
                || row[TraceDataSchema.ResponseTimestamp] is DateTime;
            if (!hasResponse)
                return null;

            var headers = DeserializeHeaders(row[TraceDataSchema.ResponseHeaders] as string);
            var payload = row[TraceDataSchema.ResponsePayload] as byte[] ?? Array.Empty<byte>();
            int? statusCode = row[TraceDataSchema.Response] is int code ? code : null;
            var reason = row[TraceDataSchema.ReasonPhrase] as string ?? string.Empty;
            var timestamp = row[TraceDataSchema.ResponseTimestamp] is DateTime ts
                ? new DateTimeOffset(DateTime.SpecifyKind(ts, DateTimeKind.Local))
                : (DateTimeOffset?)null;

            return new HttpResponse(timestamp, headers, payload, statusCode, reason);
        }

        private static string SerializeHeaders(IReadOnlyList<KeyValuePair<string, string>> headers)
        {
            if (headers is null || headers.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            for (int i = 0; i < headers.Count; i++)
            {
                var h = headers[i];
                sb.Append(h.Key);
                sb.Append(HeaderPairDelimiter);
                sb.Append(h.Value);
                if (i < headers.Count - 1)
                    sb.Append(HeaderRecordDelimiter);
            }
            return sb.ToString();
        }

        private static IReadOnlyList<KeyValuePair<string, string>> DeserializeHeaders(string? serialized)
        {
            if (string.IsNullOrEmpty(serialized))
                return Array.Empty<KeyValuePair<string, string>>();

            var records = serialized.Split(HeaderRecordDelimiter);
            var result = new List<KeyValuePair<string, string>>(records.Length);
            foreach (var record in records)
            {
                var sep = record.IndexOf(HeaderPairDelimiter);
                if (sep < 0)
                    continue;
                result.Add(new KeyValuePair<string, string>(record[..sep], record[(sep + 1)..]));
            }
            return result;
        }

        /// <summary>
        /// Opens a trace file, dispatching to the appropriate loader by extension.
        /// </summary>
        public static HttpTraceFile Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path is required.", nameof(path));
            if (!File.Exists(path))
                throw new FileNotFoundException("Trace file not found.", path);

            var ext = System.IO.Path.GetExtension(path);
            if (!Loaders.TryGetValue(ext, out var loader))
                throw new NotSupportedException($"Unsupported trace file type: '{ext}'.");

            return loader(path);
        }

        /// <summary>Registers a loader for an additional file extension (e.g. ".har").</summary>
        public static void RegisterLoader(string extension, Func<string, HttpTraceFile> loader)
        {
            if (string.IsNullOrWhiteSpace(extension))
                throw new ArgumentException("Extension is required.", nameof(extension));
            Loaders[extension] = loader ?? throw new ArgumentNullException(nameof(loader));
        }
    }
}
