using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HttpTraceAnalyser.Model
{
    /// <summary>
    /// Loads an EWS (Exchange Web Services) API trace, as produced by the EWS Managed API /
    /// PowerShell EWS scripts tracing infrastructure. There is no dedicated file extension for
    /// this format (commonly seen as <c>.trace</c>, <c>.log</c>, or <c>.txt</c>), so the loader
    /// is normally selected via content sniffing (see <see cref="LooksLikeEwsTrace"/>) rather
    /// than extension alone.
    /// </summary>
    /// <remarks>
    /// The trace is a sequence of <c>&lt;Trace Tag="..." Tid="..." Time="..."&gt;...&lt;/Trace&gt;</c>
    /// elements (not a single well-formed XML document). Each HTTP request/response is split
    /// across up to four elements, correlated by <c>Tid</c> (thread id) and encountered in
    /// sequence on that thread:
    /// <list type="bullet">
    /// <item><description><c>EwsRequestHttpHeaders</c> - raw request line + headers</description></item>
    /// <item><description><c>EwsRequest</c> - request body (SOAP XML)</description></item>
    /// <item><description><c>EwsResponseHttpHeaders</c> - raw status line + headers</description></item>
    /// <item><description><c>EwsResponse</c> - response body (SOAP XML)</description></item>
    /// </list>
    /// </remarks>
    public sealed class EwsTraceFile : HttpTraceFile
    {
        private static readonly Regex TraceElementRegex = new(
            @"<Trace\s+(?<attrs>[^>]*?)\s*>(?<body>.*?)</Trace>",
            RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex AttributeRegex = new(
            @"(?<name>\w+)\s*=\s*""(?<value>[^""]*)""",
            RegexOptions.Compiled);

        public EwsTraceFile(string filePath) : base(filePath)
        {
            var content = File.ReadAllText(filePath);
            Load(content);
        }

        /// <summary>
        /// Cheap content sniff used to identify an EWS trace file whose extension is not
        /// registered with a specific loader (e.g. <c>.log</c>, <c>.txt</c>).
        /// </summary>
        public static bool LooksLikeEwsTrace(string filePath)
        {
            try
            {
                using var reader = new StreamReader(filePath);
                var buffer = new char[8192];
                var read = reader.Read(buffer, 0, buffer.Length);
                var sample = new string(buffer, 0, read);
                return sample.Contains("<Trace Tag=\"Ews", StringComparison.Ordinal)
                    || Regex.IsMatch(sample, "<Trace\\s+Tag=\"[^\"]*\"\\s+Tid=\"", RegexOptions.None);
            }
            catch
            {
                return false;
            }
        }

        private sealed class PendingExchange
        {
            public DateTimeOffset? RequestTimestamp;
            public DateTimeOffset? ResponseTimestamp;
            public string Method = string.Empty;
            public Uri? Url;
            public List<KeyValuePair<string, string>> RequestHeaders = new();
            public byte[] RequestPayload = Array.Empty<byte>();
            public int? StatusCode;
            public string? ReasonPhrase;
            public List<KeyValuePair<string, string>> ResponseHeaders = new();
            public bool HasRequestHeaders;
        }

        private void Load(string content)
        {
            var pendingByTid = new Dictionary<string, PendingExchange>(StringComparer.Ordinal);

            foreach (Match match in TraceElementRegex.Matches(content))
            {
                var attrs = ParseAttributes(match.Groups["attrs"].Value);
                if (!attrs.TryGetValue("Tag", out var tag) || string.IsNullOrEmpty(tag))
                    continue;
                attrs.TryGetValue("Tid", out var tid);
                tid ??= string.Empty;
                var timestamp = attrs.TryGetValue("Time", out var timeText) ? ParseTime(timeText) : null;
                var body = match.Groups["body"].Value;

                if (tag.EndsWith("RequestHttpHeaders", StringComparison.OrdinalIgnoreCase))
                {
                    // Start of a new exchange on this thread; discard any incomplete previous one.
                    var pending = new PendingExchange { RequestTimestamp = timestamp, HasRequestHeaders = true };
                    var (_, method, target, headers) = ParseHttpHeaderBlock(body, isRequest: true);
                    pending.Method = method;
                    pending.RequestHeaders = headers;
                    pending.Url = BuildUrl(target, headers);
                    pendingByTid[tid] = pending;
                }
                else if (tag.EndsWith("ResponseHttpHeaders", StringComparison.OrdinalIgnoreCase))
                {
                    if (!pendingByTid.TryGetValue(tid, out var pending))
                        continue;
                    pending.ResponseTimestamp ??= timestamp;
                    var (statusLine, _, _, headers) = ParseHttpHeaderBlock(body, isRequest: false);
                    var (statusCode, reason) = ParseStatusLine(statusLine);
                    pending.StatusCode = statusCode;
                    pending.ReasonPhrase = reason;
                    pending.ResponseHeaders = headers;
                }
                else if (tag.EndsWith("Response", StringComparison.OrdinalIgnoreCase))
                {
                    if (!pendingByTid.TryGetValue(tid, out var pending))
                        continue;
                    pending.ResponseTimestamp ??= timestamp;
                    var responsePayload = Encoding.UTF8.GetBytes(body.Trim());

                    var request = new HttpRequest(
                        pending.RequestTimestamp,
                        pending.RequestHeaders,
                        pending.RequestPayload,
                        pending.Method,
                        pending.Url ?? new Uri("about:blank"));

                    var response = new HttpResponse(
                        pending.ResponseTimestamp,
                        pending.ResponseHeaders,
                        responsePayload,
                        pending.StatusCode,
                        pending.ReasonPhrase);

                    AddRow(request, response);
                    pendingByTid.Remove(tid);
                }
                else if (tag.EndsWith("Request", StringComparison.OrdinalIgnoreCase))
                {
                    // Request body (SOAP XML), correlates with the preceding *RequestHttpHeaders.
                    if (!pendingByTid.TryGetValue(tid, out var pending) || !pending.HasRequestHeaders)
                    {
                        // Headers block was missing/unmatched; start a bare exchange from the body alone.
                        pending = new PendingExchange { RequestTimestamp = timestamp };
                        pendingByTid[tid] = pending;
                    }
                    pending.RequestTimestamp ??= timestamp;
                    pending.RequestPayload = Encoding.UTF8.GetBytes(body.Trim());
                }
            }
        }

        private static Dictionary<string, string> ParseAttributes(string attrText)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in AttributeRegex.Matches(attrText))
            {
                result[m.Groups["name"].Value] = m.Groups["value"].Value;
            }
            return result;
        }

        private static DateTimeOffset? ParseTime(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            {
                return dto;
            }
            return null;
        }

        /// <summary>
        /// Parses a raw HTTP request-line/status-line + headers block.
        /// Returns (firstLine, requestTargetOrEmpty, headers).
        /// </summary>
        private static (string firstLine, string method, string target, List<KeyValuePair<string, string>> headers) ParseHttpHeaderBlock(
            string body, bool isRequest)
        {
            var lines = body
                .Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 0)
                .ToList();

            if (lines.Count == 0)
                return (string.Empty, string.Empty, string.Empty, new List<KeyValuePair<string, string>>());

            var firstLine = lines[0].Trim();
            var method = string.Empty;
            var target = string.Empty;
            if (isRequest)
            {
                var parts = firstLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    method = parts[0];
                    target = parts[1];
                }
            }

            var headers = new List<KeyValuePair<string, string>>(Math.Max(0, lines.Count - 1));
            for (int i = 1; i < lines.Count; i++)
            {
                var line = lines[i];
                var colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;
                var name = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                headers.Add(new KeyValuePair<string, string>(name, value));
            }

            return (firstLine, method, target, headers);
        }

        private static (int? statusCode, string? reason) ParseStatusLine(string statusLine)
        {
            if (string.IsNullOrWhiteSpace(statusLine))
                return (null, null);

            var parts = statusLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            {
                var reason = parts.Length == 3 ? parts[2] : null;
                return (code, reason);
            }
            return (null, null);
        }

        private static Uri BuildUrl(string target, IReadOnlyList<KeyValuePair<string, string>> headers)
        {
            if (string.IsNullOrEmpty(target))
                return new Uri("about:blank");

            if (Uri.TryCreate(target, UriKind.Absolute, out var absolute))
                return absolute;

            var host = headers.FirstOrDefault(h =>
                string.Equals(h.Key, "Host", StringComparison.OrdinalIgnoreCase)).Value;

            if (!string.IsNullOrWhiteSpace(host))
            {
                var path = target.StartsWith('/') ? target : "/" + target;
                if (Uri.TryCreate($"https://{host}{path}", UriKind.Absolute, out var built))
                    return built;
            }

            return new Uri(target, UriKind.RelativeOrAbsolute);
        }
    }
}
