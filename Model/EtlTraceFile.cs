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
            "Microsoft-Windows-AAD",
            "Microsoft.Windows.Security.TokenBroker",
        };

        // Providers that log token/auth operations rather than raw HTTP. Rows extracted
        // from these get a default method of "TOKEN" when the payload has no HTTP verb
        // (which is normally the case), plus richer synthesised header fields.
        private static readonly HashSet<string> AuthProviders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft-Windows-AAD",
            "Microsoft.Windows.Security.TokenBroker",
        };

        // Payload field names we probe for, in preference order.
        private static readonly string[] MethodFieldNames = { "Verb", "Method", "HttpVerb" };
        private static readonly string[] UrlFieldNames = { "Url", "URL", "Uri", "URI", "Object", "FullUrl", "TargetUrl", "Endpoint", "Authority", "Resource", "ResourceUri", "ResourceUrl" };
        private static readonly string[] StatusFieldNames = { "StatusCode", "Status", "HttpStatusCode" };
        private static readonly string[] HeaderFieldNames = { "HeaderText", "Headers", "RequestHeaders", "ResponseHeaders" };
        private static readonly string[] HostFieldNames = { "ServerName", "Host", "HostName" };

        // Auth-provider probes: not real HTTP fields, but surfaced as pseudo-headers so
        // they render in the Request/Response headers view for troubleshooting.
        private static readonly string[] CorrelationFieldNames = { "CorrelationId", "ClientRequestId", "client-request-id", "XMsRequestId", "x-ms-request-id", "ActivityId", "RequestId" };
        private static readonly string[] ErrorCodeFieldNames = { "ErrorCode", "OAuthErrorCode", "HResult", "HRESULT", "NtStatus", "Result" };
        private static readonly string[] ErrorDescriptionFieldNames = { "ErrorDescription", "ErrorMessage", "Message", "ErrorText" };
        private static readonly string[] IdentityFieldNames = { "UserId", "AccountId", "Upn", "UserPrincipalName", "ClientId", "AppId", "ProviderId", "Scope", "Scopes" };

        public EtlTraceFile(string filePath) : base(filePath)
        {
            Load(filePath);
        }

        private void Load(string filePath)
        {
            using var source = new ETWTraceEventSource(filePath);
            var pending = new Dictionary<Guid, PendingRequest>();
            var completed = new List<PendingRequest>();
            var providerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            source.Dynamic.All += data =>
            {
                var providerName = string.IsNullOrEmpty(data.ProviderName) ? "(unknown)" : data.ProviderName;
                providerCounts.TryGetValue(providerName, out var seen);
                providerCounts[providerName] = seen + 1;

                if (!HttpProviders.Contains(providerName))
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
                if (AuthProviders.Contains(providerName))
                {
                    pr.IsAuthEvent = true;
                    pr.Provider ??= providerName;
                }

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

            ProviderEventCounts = providerCounts;

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

            // Auth-provider extras: surfaced as pseudo-headers so they appear in the
            // Request headers view even though they aren't real HTTP headers.
            if (pr.IsAuthEvent)
            {
                CaptureFirst(data, CorrelationFieldNames, pr.RequestExtras, "X-Correlation-Id");
                CaptureFirst(data, IdentityFieldNames, pr.RequestExtras, "X-Identity");
                CaptureFirst(data, ErrorCodeFieldNames, pr.ResponseExtras, "X-Error-Code");
                CaptureFirst(data, ErrorDescriptionFieldNames, pr.ResponseExtras, "X-Error-Description");

                if (pr.ResponseExtras.Count > 0 && pr.ResponseSeen == default)
                    pr.ResponseSeen = data.TimeStamp;

                var eventNameHere = data.EventName ?? string.Empty;
                if (!pr.RequestExtras.ContainsKey("X-Event") && !string.IsNullOrEmpty(eventNameHere))
                    pr.RequestExtras["X-Event"] = eventNameHere;
                if (!pr.RequestExtras.ContainsKey("X-Provider") && !string.IsNullOrEmpty(pr.Provider))
                    pr.RequestExtras["X-Provider"] = pr.Provider!;
            }
        }

        private static void CaptureFirst(TraceEvent data, string[] fieldNames,
            Dictionary<string, string> target, string headerName)
        {
            if (target.ContainsKey(headerName))
                return;
            foreach (var name in fieldNames)
            {
                var v = TryGetString(data, name);
                if (string.IsNullOrEmpty(v) || v == "0")
                    continue;
                target[headerName] = v;
                return;
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

            // Auth-provider augmentation.
            public bool IsAuthEvent { get; set; }
            public string? Provider { get; set; }
            public Dictionary<string, string> RequestExtras { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> ResponseExtras { get; } = new(StringComparer.OrdinalIgnoreCase);

            public bool HasEnoughForRow()
                => !string.IsNullOrEmpty(Url) || !string.IsNullOrEmpty(Method) ||
                   StatusCode is not null ||
                   (IsAuthEvent && (RequestExtras.Count > 0 || ResponseExtras.Count > 0));

            public HttpRequest BuildRequest()
            {
                var method = Method ?? (IsAuthEvent ? "TOKEN" : string.Empty);
                var urlText = Url ?? (Host is not null ? "http://" + Host + "/" : "about:blank");
                var uri = Uri.TryCreate(urlText, UriKind.Absolute, out var abs)
                    ? abs
                    : new Uri(urlText, UriKind.RelativeOrAbsolute);

                var headers = MergeHeaders(
                    ParseHttpHeaders(RequestHeaderText, out _, out _, out _),
                    RequestExtras);
                return new HttpRequest(FirstSeen, headers, Array.Empty<byte>(), method, uri);
            }

            public HttpResponse? BuildResponse()
            {
                if (StatusCode is null && string.IsNullOrEmpty(ResponseHeaderText) && ResponseExtras.Count == 0)
                    return null;

                int? status = StatusCode;
                string? reason = null;
                var headers = MergeHeaders(
                    ParseHttpHeaders(ResponseHeaderText, out var parsedStatus, out var parsedReason, out _),
                    ResponseExtras);
                if (status is null && parsedStatus is not null)
                {
                    status = parsedStatus;
                    reason = parsedReason;
                }

                var timestamp = ResponseSeen != default ? ResponseSeen : LastSeen;
                return new HttpResponse(timestamp, headers, Array.Empty<byte>(), status, reason);
            }

            private static IReadOnlyList<KeyValuePair<string, string>> MergeHeaders(
                IReadOnlyList<KeyValuePair<string, string>> parsed,
                Dictionary<string, string> extras)
            {
                if (extras.Count == 0)
                    return parsed;
                var list = new List<KeyValuePair<string, string>>(parsed.Count + extras.Count);
                list.AddRange(parsed);
                foreach (var kvp in extras)
                    list.Add(new KeyValuePair<string, string>(kvp.Key, kvp.Value));
                return list;
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
