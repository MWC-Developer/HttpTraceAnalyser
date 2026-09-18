using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace HttpTraceAnalyser.Model
{
    /// <summary>
    /// Loads Chromium NetLog exports generated with the --log-net-log command-line switch.
    /// NetLog records protocol events rather than bodies, so this parser exposes the request and
    /// response metadata and headers that Chromium captured for each URL request.
    /// </summary>
    public sealed class NetLogTraceFile : HttpTraceFile
    {
        private const string UrlRequestStartJob = "URL_REQUEST_START_JOB";

        public NetLogTraceFile(string filePath) : base(filePath)
        {
            using var stream = File.OpenRead(filePath);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;

            if (!IsNetLog(root))
                throw new InvalidDataException("The JSON document is not a Chromium NetLog export.");

            var eventTypes = GetEventTypes(root.GetProperty("constants"));
            var startTimestamp = GetCaptureStartTimestamp(root.GetProperty("constants"));
            var requests = new Dictionary<long, PendingRequest>();
            var requestLegsBySource = new Dictionary<long, List<PendingRequest>>();
            var allRequests = new List<PendingRequest>();
            var urlRequestSourceIds = new HashSet<long>();
            var requestsByHttp2Stream = new Dictionary<(long SourceId, long StreamId), PendingRequest>();

            // Register every URL request before inspecting its related transaction events.
            foreach (var entry in root.GetProperty("events").EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !TryGetInt64(entry, "type", out var type) ||
                    !eventTypes.TryGetValue(type, out var eventName) ||
                    !TryGetSourceId(entry, out var sourceId))
                {
                    continue;
                }

                if ((string.Equals(eventName, UrlRequestStartJob, StringComparison.Ordinal) ||
                     TryGetSourceType(entry, out var sourceType) && sourceType == 1) &&
                    TryCreateRequest(entry, startTimestamp, out var request))
                {
                    if (!requestLegsBySource.TryGetValue(sourceId, out var legs))
                    {
                        legs = new List<PendingRequest>();
                        requestLegsBySource[sourceId] = legs;
                    }

                    var existing = legs.LastOrDefault();
                    if (existing is null || !Uri.Equals(existing.Url, request.Url))
                    {
                        legs.Add(request);
                        allRequests.Add(request);
                        requests.TryAdd(sourceId, request);
                    }
                    else if (string.IsNullOrEmpty(existing.Method) && !string.IsNullOrEmpty(request.Method))
                    {
                        existing.Method = request.Method;
                    }

                    urlRequestSourceIds.Add(sourceId);
                }
            }

            // Chromium emits URL_REQUEST events and the corresponding HTTP transaction
            // events under different source IDs. URL_REQUEST's source_dependency points
            // to the transaction, whose events contain the actual request/response headers.
            // Resolve all links first because NetLog does not guarantee event ordering.
            foreach (var entry in root.GetProperty("events").EnumerateArray())
            {
                if (!TryGetSourceId(entry, out var sourceId) ||
                    !urlRequestSourceIds.Contains(sourceId) ||
                    !TryGetSourceDependencyId(entry, out var dependencyId))
                {
                    continue;
                }

                var sourceRequest = ResolveRequest(
                    sourceId,
                    GetTimestamp(entry, startTimestamp),
                    requestLegsBySource,
                    requests);
                if (sourceRequest is not null)
                    requests.TryAdd(dependencyId, sourceRequest);
            }

            // A transaction can be linked through an intermediate stream-job source. Record
            // those sources, but never alias an HTTP/2 session itself because it serves many
            // requests concurrently.
            foreach (var entry in root.GetProperty("events").EnumerateArray())
            {
                if (!TryGetSourceId(entry, out var sourceId) ||
                    !TryGetSourceDependencyId(entry, out var dependencyId) ||
                    !entry.TryGetProperty("params", out var parameters) ||
                    parameters.ValueKind != JsonValueKind.Object ||
                    parameters.TryGetProperty("stream_id", out _))
                {
                    continue;
                }

                var request = ResolveRequest(
                    dependencyId,
                    GetTimestamp(entry, startTimestamp),
                    requestLegsBySource,
                    requests);
                if (request is not null)
                    requests.TryAdd(sourceId, request);
            }

            // Cached responses are represented by a separate cache-entry source. Its cache
            // key contains the requested URL, which is the only correlation NetLog preserves
            // for these entries.
            foreach (var entry in root.GetProperty("events").EnumerateArray())
            {
                if (!TryGetSourceId(entry, out var sourceId) ||
                    !entry.TryGetProperty("params", out var parameters) ||
                    !TryGetString(parameters, "key", out var cacheKey))
                {
                    continue;
                }

                var eventTimestamp = GetTimestamp(entry, startTimestamp);
                var request = FindMostRecentRequest(
                    allRequests,
                    eventTimestamp,
                    candidate => cacheKey!.EndsWith(candidate.Url.AbsoluteUri, StringComparison.Ordinal));
                if (request is not null)
                    requests.TryAdd(sourceId, request);
            }

            // HTTP/2 and HTTP/3 request header events contain the authoritative request
            // identity. Use it to correlate protocol events even when Chromium's dependency
            // graph omits or reorders the URL-request-to-transaction link.
            foreach (var entry in root.GetProperty("events").EnumerateArray())
            {
                if (!TryGetSourceId(entry, out var sourceId) ||
                    !entry.TryGetProperty("params", out var parameters))
                {
                    continue;
                }

                var headers = ParseHeaders(parameters);
                if (!TryFindRequestForHeaders(
                    headers,
                    allRequests,
                    GetTimestamp(entry, startTimestamp),
                    out var request))
                    continue;

                if (TryGetInt64(parameters, "stream_id", out var streamId))
                    requestsByHttp2Stream.TryAdd((sourceId, streamId), request);
                else
                    requests.TryAdd(sourceId, request);
            }

            // HTTP/2 header events originate from the shared session. The stream ID and its
            // source dependency identify the individual transaction that owns those headers.
            foreach (var entry in root.GetProperty("events").EnumerateArray())
            {
                if (!TryGetSourceId(entry, out var sourceId) ||
                    !TryGetSourceDependencyId(entry, out var dependencyId) ||
                    !entry.TryGetProperty("params", out var parameters) ||
                    !TryGetInt64(parameters, "stream_id", out var streamId))
                {
                    continue;
                }

                var request = ResolveRequest(
                    dependencyId,
                    GetTimestamp(entry, startTimestamp),
                    requestLegsBySource,
                    requests);
                if (request is not null)
                    requestsByHttp2Stream.TryAdd((sourceId, streamId), request);
            }

            foreach (var entry in root.GetProperty("events").EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !TryGetInt64(entry, "type", out var type) ||
                    !eventTypes.TryGetValue(type, out var eventName) ||
                    !TryGetSourceId(entry, out var sourceId))
                {
                    continue;
                }

                if (!entry.TryGetProperty("params", out var parameters) ||
                    parameters.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var eventTimestamp = GetTimestamp(entry, startTimestamp);
                var pending = ResolveRequest(sourceId, eventTimestamp, requestLegsBySource, requests);
                if (pending is null &&
                    (!TryGetInt64(parameters, "stream_id", out var streamId) ||
                     !requestsByHttp2Stream.TryGetValue((sourceId, streamId), out pending)) &&
                    (!TryGetString(parameters, "url", out var urlText) ||
                     (pending = FindMostRecentRequest(
                         allRequests,
                         eventTimestamp,
                         candidate => string.Equals(candidate.Url.AbsoluteUri, urlText, StringComparison.Ordinal))) is null))
                {
                    continue;
                }

                if (TryGetPayload(parameters, out var payload))
                {
                    if (IsRequestPayloadEvent(eventName))
                        pending.RequestPayload.AddRange(payload);
                    else if (IsFilteredResponsePayloadEvent(eventName))
                    {
                        pending.FilteredResponsePayload.AddRange(payload);
                        pending.ResponseTimestamp ??= GetTimestamp(entry, startTimestamp);
                    }
                    else if (IsRawResponsePayloadEvent(eventName))
                    {
                        pending.ResponsePayload.AddRange(payload);
                        pending.ResponseTimestamp ??= GetTimestamp(entry, startTimestamp);
                    }
                }

                var headers = ParseHeaders(parameters);
                if (headers.Count == 0)
                    continue;

                var isRequestHeaders = eventName.EndsWith("SEND_REQUEST_HEADERS", StringComparison.Ordinal) ||
                    parameters.TryGetProperty("request_headers", out _) ||
                    parameters.TryGetProperty("requestHeaders", out _) ||
                    headers.Any(header => string.Equals(header.Key, ":method", StringComparison.OrdinalIgnoreCase));
                var isResponseHeaders = eventName.EndsWith("RESPONSE_HEADERS", StringComparison.Ordinal) ||
                    eventName.EndsWith("RECV_HEADERS", StringComparison.Ordinal) ||
                    LooksLikeResponseHeaders(parameters, headers);

                if (isRequestHeaders && !isResponseHeaders)
                {
                    pending.RequestHeaders = headers;
                }
                else if (isResponseHeaders)
                {
                    pending.ResponseHeaders = headers;
                    (pending.StatusCode, pending.ReasonPhrase) = ParseStatus(parameters, headers);
                    pending.ResponseTimestamp = GetTimestamp(entry, startTimestamp);
                }
            }

            foreach (var pending in allRequests)
            {
                var responsePayload = pending.GetResponsePayload();
                HttpResponse? response = null;
                if (pending.ResponseHeaders is { Count: > 0 } || responsePayload.Length > 0)
                {
                    response = new HttpResponse(
                        pending.ResponseTimestamp,
                        pending.ResponseHeaders ?? new List<KeyValuePair<string, string>>(),
                        responsePayload,
                        pending.StatusCode,
                        pending.ReasonPhrase);
                }

                AddRow(new HttpRequest(
                    pending.RequestTimestamp,
                    pending.RequestHeaders ?? new List<KeyValuePair<string, string>>(),
                    pending.RequestPayload.ToArray(),
                    pending.Method,
                    pending.Url), response);
            }
        }

        public static bool LooksLikeNetLog(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var document = JsonDocument.Parse(stream);
                return IsNetLog(document.RootElement);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool IsNetLog(JsonElement root)
            => root.ValueKind == JsonValueKind.Object &&
               root.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array &&
               root.TryGetProperty("constants", out var constants) && constants.ValueKind == JsonValueKind.Object &&
               constants.TryGetProperty("logEventTypes", out var eventTypes) && eventTypes.ValueKind == JsonValueKind.Object;

        private static Dictionary<long, string> GetEventTypes(JsonElement constants)
        {
            var result = new Dictionary<long, string>();
            foreach (var property in constants.GetProperty("logEventTypes").EnumerateObject())
            {
                if (TryGetInt64(property.Value, out var value))
                    result[value] = property.Name;
            }
            return result;
        }

        private static DateTimeOffset GetCaptureStartTimestamp(JsonElement constants)
        {
            if (constants.TryGetProperty("timeTickOffset", out var offset) && TryGetInt64(offset, out var milliseconds))
                return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);

            return DateTimeOffset.UnixEpoch;
        }

        private static bool TryCreateRequest(JsonElement entry, DateTimeOffset captureStart, out PendingRequest request)
        {
            request = null!;
            if (!entry.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object ||
                !TryGetString(parameters, "url", out var urlText) ||
                !Uri.TryCreate(urlText, UriKind.Absolute, out var url))
            {
                return false;
            }

            TryGetString(parameters, "method", out var method);
            request = new PendingRequest(GetTimestamp(entry, captureStart), method ?? string.Empty, url);
            return true;
        }

        private static DateTimeOffset? GetTimestamp(JsonElement entry, DateTimeOffset captureStart)
        {
            if (!TryGetInt64(entry, "time", out var microseconds))
                return null;

            return captureStart.AddTicks(microseconds * 10);
        }

        private static bool TryGetSourceId(JsonElement entry, out long sourceId)
        {
            sourceId = default;
            return entry.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object &&
                   TryGetInt64(source, "id", out sourceId);
        }

        private static bool TryGetSourceType(JsonElement entry, out long sourceType)
        {
            sourceType = default;
            return entry.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object &&
                   TryGetInt64(source, "type", out sourceType);
        }

        private static bool TryGetSourceDependencyId(JsonElement entry, out long sourceId)
        {
            sourceId = default;
            return entry.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object &&
                   parameters.TryGetProperty("source_dependency", out var dependency) && dependency.ValueKind == JsonValueKind.Object &&
                   TryGetInt64(dependency, "id", out sourceId);
        }

        private static List<KeyValuePair<string, string>> ParseHeaders(JsonElement parameters)
        {
            var headers = new List<KeyValuePair<string, string>>();
            if (!TryGetHeaderValues(parameters, out var values))
                return headers;

            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String)
                    continue;

                var line = value.GetString();
                if (string.IsNullOrEmpty(line))
                    continue;

                var separator = line.IndexOf(':');
                if (separator < 0)
                    continue;

                if (separator == 0)
                    separator = line.IndexOf(':', 1);
                if (separator <= 0)
                    continue;

                headers.Add(new KeyValuePair<string, string>(line[..separator].Trim(), line[(separator + 1)..].Trim()));
            }

            return headers;
        }

        private static bool TryGetHeaderValues(JsonElement parameters, out JsonElement values)
        {
            if (parameters.TryGetProperty("headers", out values) && values.ValueKind == JsonValueKind.Array)
                return true;

            foreach (var name in new[] { "request_headers", "response_headers", "requestHeaders", "responseHeaders" })
            {
                if (parameters.TryGetProperty(name, out var container) && container.ValueKind == JsonValueKind.Object &&
                    container.TryGetProperty("headers", out values) && values.ValueKind == JsonValueKind.Array)
                {
                    return true;
                }
            }

            values = default;
            return false;
        }

        private static bool LooksLikeResponseHeaders(
            JsonElement parameters, IReadOnlyList<KeyValuePair<string, string>> headers)
        {
            if (headers.Any(header => string.Equals(header.Key, ":status", StringComparison.OrdinalIgnoreCase)))
                return true;

            if (parameters.TryGetProperty("response_headers", out var responseHeaders) &&
                responseHeaders.ValueKind == JsonValueKind.Object)
            {
                return true;
            }

            return TryGetHeaderValues(parameters, out var values) && values.EnumerateArray().Any(value =>
                value.ValueKind == JsonValueKind.String &&
                value.GetString()?.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase) == true);
        }

        private static bool TryFindRequestForHeaders(
            IReadOnlyList<KeyValuePair<string, string>> headers,
            IEnumerable<PendingRequest> candidates,
            DateTimeOffset? eventTimestamp,
            out PendingRequest request)
        {
            request = null!;
            var method = headers.FirstOrDefault(header =>
                string.Equals(header.Key, ":method", StringComparison.OrdinalIgnoreCase)).Value;
            var authority = headers.FirstOrDefault(header =>
                string.Equals(header.Key, ":authority", StringComparison.OrdinalIgnoreCase)).Value;
            var path = headers.FirstOrDefault(header =>
                string.Equals(header.Key, ":path", StringComparison.OrdinalIgnoreCase)).Value;
            var scheme = headers.FirstOrDefault(header =>
                string.Equals(header.Key, ":scheme", StringComparison.OrdinalIgnoreCase)).Value;

            if (string.IsNullOrWhiteSpace(method) || string.IsNullOrWhiteSpace(authority) || string.IsNullOrWhiteSpace(path))
                return false;
            if (string.IsNullOrWhiteSpace(scheme))
                scheme = "https";
            if (!Uri.TryCreate($"{scheme}://{authority}{path}", UriKind.Absolute, out var url))
                return false;

            request = FindMostRecentRequest(
                candidates,
                eventTimestamp,
                candidate =>
                    (string.IsNullOrEmpty(candidate.Method) ||
                     string.Equals(candidate.Method, method, StringComparison.OrdinalIgnoreCase)) &&
                    Uri.Compare(candidate.Url, url, UriComponents.HttpRequestUrl, UriFormat.SafeUnescaped,
                        StringComparison.OrdinalIgnoreCase) == 0)!;
            if (request is not null && string.IsNullOrEmpty(request.Method))
                request.Method = method;
            return request is not null;
        }

        private static PendingRequest? FindMostRecentRequest(
            IEnumerable<PendingRequest> candidates,
            DateTimeOffset? eventTimestamp,
            Func<PendingRequest, bool> predicate)
        {
            var matches = candidates.Distinct().Where(predicate);
            if (eventTimestamp.HasValue)
                matches = matches.Where(candidate => !candidate.RequestTimestamp.HasValue || candidate.RequestTimestamp <= eventTimestamp);

            return matches
                .OrderByDescending(candidate => candidate.RequestTimestamp ?? DateTimeOffset.MinValue)
                .FirstOrDefault();
        }

        private static PendingRequest? ResolveRequest(
            long sourceId,
            DateTimeOffset? eventTimestamp,
            IReadOnlyDictionary<long, List<PendingRequest>> requestLegsBySource,
            IReadOnlyDictionary<long, PendingRequest> requestsByAlias)
        {
            if (requestLegsBySource.TryGetValue(sourceId, out var legs))
            {
                return FindMostRecentRequest(
                    legs,
                    eventTimestamp,
                    _ => true);
            }

            return requestsByAlias.TryGetValue(sourceId, out var request) ? request : null;
        }

        private static bool TryGetPayload(JsonElement parameters, out byte[] payload)
        {
            payload = Array.Empty<byte>();
            if (!TryGetString(parameters, "bytes", out var encoded) || string.IsNullOrEmpty(encoded))
                return false;

            try
            {
                payload = Convert.FromBase64String(encoded);
            }
            catch (FormatException)
            {
                // Preserve non-base64 payloads emitted by compatible NetLog producers.
                payload = Encoding.UTF8.GetBytes(encoded);
            }

            return payload.Length > 0;
        }

        private static bool IsRequestPayloadEvent(string eventName)
            => eventName.Contains("UPLOAD", StringComparison.Ordinal) ||
               eventName.EndsWith("SEND_BODY", StringComparison.Ordinal);

        private static bool IsFilteredResponsePayloadEvent(string eventName)
            => string.Equals(eventName, "URL_REQUEST_JOB_FILTERED_BYTES_READ", StringComparison.Ordinal);

        private static bool IsRawResponsePayloadEvent(string eventName)
            => string.Equals(eventName, "URL_REQUEST_JOB_BYTES_READ", StringComparison.Ordinal) ||
               eventName.EndsWith("RECV_BODY", StringComparison.Ordinal);

        private static (int? StatusCode, string ReasonPhrase) ParseStatus(
            JsonElement parameters,
            IReadOnlyList<KeyValuePair<string, string>> headers)
        {
            if (TryGetHeaderValues(parameters, out var values))
            {
                foreach (var value in values.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.String)
                        continue;

                    var statusLine = value.GetString();
                    if (!string.IsNullOrEmpty(statusLine) && statusLine.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = statusLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var statusCode))
                            return (statusCode, parts.Length == 3 ? parts[2] : string.Empty);
                    }
                }
            }

            foreach (var header in headers)
            {
                if (string.Equals(header.Key, ":status", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(header.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var http2Status))
                {
                    return (http2Status, string.Empty);
                }
            }

            return (null, string.Empty);
        }

        private static bool TryGetString(JsonElement element, string propertyName, out string? value)
        {
            value = null;
            return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String &&
                   (value = property.GetString()) is not null;
        }

        private static bool TryGetInt64(JsonElement element, string propertyName, out long value)
        {
            if (element.TryGetProperty(propertyName, out var property))
                return TryGetInt64(property, out value);

            value = default;
            return false;
        }

        private static bool TryGetInt64(JsonElement value, out long result)
        {
            if (value.ValueKind == JsonValueKind.Number)
                return value.TryGetInt64(out result);
            if (value.ValueKind == JsonValueKind.String)
                return long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

            result = default;
            return false;
        }

        private sealed class PendingRequest
        {
            public PendingRequest(DateTimeOffset? requestTimestamp, string method, Uri url)
            {
                RequestTimestamp = requestTimestamp;
                Method = method;
                Url = url;
            }

            public DateTimeOffset? RequestTimestamp { get; }
            public string Method { get; set; }
            public Uri Url { get; }
            public List<KeyValuePair<string, string>>? RequestHeaders { get; set; }
            public List<KeyValuePair<string, string>>? ResponseHeaders { get; set; }
            public List<byte> RequestPayload { get; } = new();
            public List<byte> ResponsePayload { get; } = new();
            public List<byte> FilteredResponsePayload { get; } = new();
            public DateTimeOffset? ResponseTimestamp { get; set; }
            public int? StatusCode { get; set; }
            public string ReasonPhrase { get; set; } = string.Empty;

            public byte[] GetResponsePayload()
            {
                var hasContentEncoding = ResponseHeaders?.Any(header =>
                    string.Equals(header.Key, "Content-Encoding", StringComparison.OrdinalIgnoreCase)) == true;
                if (hasContentEncoding && ResponsePayload.Count > 0)
                    return ResponsePayload.ToArray();
                if (FilteredResponsePayload.Count > 0)
                    return FilteredResponsePayload.ToArray();
                return ResponsePayload.ToArray();
            }
        }
    }
}
