using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Diagnostics.Tracing;

namespace HttpTraceAnalyser.Model
{
    /// <summary>
    /// Loads an Event Trace for Windows (.etl) capture and extracts HTTP request/response
    /// pairs from the standard Windows HTTP providers (WinHTTP, WinINet, HTTP.SYS).
    ///
    /// This is a best-effort extractor: ETW events for these providers vary by Windows
    /// version and rarely carry request/response bodies, so extracted entries are typically
    /// header-level. Traces produced by <c>netsh trace start scenario=InternetClient</c>
    /// or targeted logman sessions on the WinHTTP/WinINet/HTTPService providers give the
    /// richest results.
    /// </summary>
    public sealed class EtlTraceFile : HttpTraceFile
    {
        // Providers we recognise; matched by ProviderName so that both the classic and
        // manifest forms of the trace resolve. Case-insensitive comparisons throughout.
        private static readonly HashSet<string> HttpProviders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft-Windows-WinHTTP",
            "Microsoft-Windows-WinINet",
            "Microsoft-Windows-WinINet-Capture",
            "Microsoft-Windows-HttpService",
        };

        // Payload field names we probe for, in preference order.
        private static readonly string[] MethodFieldNames = { "Verb", "Method", "HttpVerb" };
        private static readonly string[] UrlFieldNames = { "Url", "URL", "Uri", "URI", "Object", "FullUrl" };
        private static readonly string[] StatusFieldNames = { "StatusCode", "Status", "HttpStatusCode" };
        private static readonly string[] HeaderFieldNames = { "HeaderText", "Headers", "RequestHeaders", "ResponseHeaders" };
        private static readonly string[] HostFieldNames = { "ServerName", "Host", "HostName" };

        public EtlTraceFile(string filePath) : base(filePath)
        {
            Load(filePath);
        }

        private void Load(string filePath)
        {
            using var source = new ETWTraceEventSource(filePath);
            var pending = new Dictionary<Guid, PendingRequest>();
            var completed = new List<PendingRequest>();

            source.Dynamic.All += data =>
            {
                if (!HttpProviders.Contains(data.ProviderName))
                    return;

                var key = data.ActivityID;
                if (key == Guid.Empty)
                    return; // No correlation possible; skip.

                if (!pending.TryGetValue(key, out var pr))
                {
                    pr = new PendingRequest { ActivityId = key, FirstSeen = data.TimeStamp };
                    pending[key] = pr;
                }
                pr.LastSeen = data.TimeStamp;

                ExtractInto(data, pr);

                // Treat any Stop/End/Complete opcode as a terminal event for the correlation.
                var opcode = data.Opcode;
                var eventName = data.EventName ?? string.Empty;
                bool isTerminal =
                    opcode == TraceEventOpcode.Stop ||
                    eventName.EndsWith("Stop", StringComparison.OrdinalIgnoreCase) ||
                    eventName.EndsWith("Complete", StringComparison.OrdinalIgnoreCase) ||
                    eventName.EndsWith("End", StringComparison.OrdinalIgnoreCase) ||
                    eventName.IndexOf("SendComplete", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    eventName.IndexOf("FastResp", StringComparison.OrdinalIgnoreCase) >= 0;

                if (isTerminal && pr.HasEnoughForRow())
                {
                    pending.Remove(key);
                    completed.Add(pr);
                }
            };

            source.Process();

            // Flush any correlations that never got a terminal event but have usable data.
            foreach (var pr in pending.Values)
            {
                if (pr.HasEnoughForRow())
                    completed.Add(pr);
            }

            foreach (var pr in completed.OrderBy(p => p.FirstSeen))
                AddRow(pr.BuildRequest(), pr.BuildResponse());
        }

        private static void ExtractInto(TraceEvent data, PendingRequest pr)
        {
            // Method
            if (pr.Method is null)
            {
                foreach (var name in MethodFieldNames)
                {
                    var v = TryGetString(data, name);
                    if (!string.IsNullOrEmpty(v)) { pr.Method = v; break; }
                }
            }

            // URL
            if (pr.Url is null)
            {
                foreach (var name in UrlFieldNames)
                {
                    var v = TryGetString(data, name);
                    if (!string.IsNullOrEmpty(v)) { pr.Url = v; break; }
                }
            }

            // Host (kept only if URL is relative or missing).
            if (pr.Host is null)
            {
                foreach (var name in HostFieldNames)
                {
                    var v = TryGetString(data, name);
                    if (!string.IsNullOrEmpty(v)) { pr.Host = v; break; }
                }
            }

            // Status code — response side.
            if (pr.StatusCode is null)
            {
                foreach (var name in StatusFieldNames)
                {
                    if (TryGetInt(data, name, out var code) && code > 0)
                    {
                        pr.StatusCode = code;
                        pr.ResponseSeen = data.TimeStamp;
                        break;
                    }
                }
            }

            // Header text — try both request and response depending on event direction.
            foreach (var name in HeaderFieldNames)
            {
                var headers = TryGetString(data, name);
                if (string.IsNullOrEmpty(headers))
                    continue;

                bool looksLikeResponse =
                    headers.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase);

                if (looksLikeResponse && pr.ResponseHeaderText is null)
                {
                    pr.ResponseHeaderText = headers;
                    if (pr.ResponseSeen == default)
                        pr.ResponseSeen = data.TimeStamp;
                }
                else if (!looksLikeResponse && pr.RequestHeaderText is null)
                {
                    pr.RequestHeaderText = headers;
                }
                break;
            }
        }

        private static string? TryGetString(TraceEvent data, string fieldName)
        {
            try
            {
                var raw = data.PayloadByName(fieldName);
                return raw?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static bool TryGetInt(TraceEvent data, string fieldName, out int value)
        {
            value = 0;
            try
            {
                var raw = data.PayloadByName(fieldName);
                if (raw is null) return false;
                if (raw is int i) { value = i; return true; }
                if (raw is uint u) { value = (int)u; return true; }
                if (raw is short s) { value = s; return true; }
                if (raw is ushort us) { value = us; return true; }
                if (raw is long l) { value = (int)l; return true; }
                return int.TryParse(raw.ToString(), out value);
            }
            catch
            {
                return false;
            }
        }

        private sealed class PendingRequest
        {
            public Guid ActivityId { get; init; }
            public DateTime FirstSeen { get; set; }
            public DateTime LastSeen { get; set; }
            public DateTime ResponseSeen { get; set; }

            public string? Method { get; set; }
            public string? Url { get; set; }
            public string? Host { get; set; }
            public int? StatusCode { get; set; }
            public string? RequestHeaderText { get; set; }
            public string? ResponseHeaderText { get; set; }

            public bool HasEnoughForRow()
                => !string.IsNullOrEmpty(Url) || !string.IsNullOrEmpty(Method) || StatusCode is not null;

            public HttpRequest BuildRequest()
            {
                var method = Method ?? string.Empty;
                var urlText = Url ?? (Host is not null ? "http://" + Host + "/" : "about:blank");
                var uri = Uri.TryCreate(urlText, UriKind.Absolute, out var abs)
                    ? abs
                    : new Uri(urlText, UriKind.RelativeOrAbsolute);

                var headers = ParseHttpHeaders(RequestHeaderText, out _, out _, out _);
                return new HttpRequest(FirstSeen, headers, Array.Empty<byte>(), method, uri);
            }

            public HttpResponse? BuildResponse()
            {
                if (StatusCode is null && string.IsNullOrEmpty(ResponseHeaderText))
                    return null;

                int? status = StatusCode;
                string? reason = null;
                var headers = ParseHttpHeaders(ResponseHeaderText, out var parsedStatus, out var parsedReason, out _);
                if (status is null && parsedStatus is not null)
                {
                    status = parsedStatus;
                    reason = parsedReason;
                }

                var timestamp = ResponseSeen != default ? ResponseSeen : LastSeen;
                return new HttpResponse(timestamp, headers, Array.Empty<byte>(), status, reason);
            }
        }

        // Minimal parser for a block of raw HTTP header text (CRLF or LF terminated lines,
        // first line optionally a request/status line).
        private static IReadOnlyList<KeyValuePair<string, string>> ParseHttpHeaders(
            string? headerText,
            out int? statusCode,
            out string? reasonPhrase,
            out string? startLine)
        {
            statusCode = null;
            reasonPhrase = null;
            startLine = null;

            if (string.IsNullOrEmpty(headerText))
                return Array.Empty<KeyValuePair<string, string>>();

            var lines = headerText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var result = new List<KeyValuePair<string, string>>(lines.Length);

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrEmpty(line))
                    continue;

                if (i == 0)
                {
                    startLine = line;
                    if (line.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && int.TryParse(parts[1], out var code))
                        {
                            statusCode = code;
                            if (parts.Length == 3)
                                reasonPhrase = parts[2];
                        }
                        continue;
                    }
                    // If it looks like "METHOD SP TARGET SP HTTP/x", skip it as a request line.
                    if (line.IndexOf(" HTTP/", StringComparison.OrdinalIgnoreCase) > 0)
                        continue;
                }

                var colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;
                var name = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                result.Add(new KeyValuePair<string, string>(name, value));
            }

            return result;
        }
    }
}
