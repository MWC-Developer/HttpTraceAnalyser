using System;
using System.Collections.Generic;

namespace HttpTraceAnalyser.Model
{
    /// <summary>
    /// Base class for a single HTTP message (request or response) captured in a trace.
    /// </summary>
    public abstract class HttpMessage
    {
        protected HttpMessage(
            DateTimeOffset? timestamp,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            byte[] payload)
        {
            Timestamp = timestamp;
            Headers = headers ?? Array.Empty<KeyValuePair<string, string>>();
            Payload = payload ?? Array.Empty<byte>();
        }

        /// <summary>Time the message was captured, if known.</summary>
        public DateTimeOffset? Timestamp { get; }

        /// <summary>Headers in original order (duplicates preserved).</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

        /// <summary>Raw message body bytes.</summary>
        public byte[] Payload { get; }
    }

    public sealed class HttpRequest : HttpMessage
    {
        public HttpRequest(
            DateTimeOffset? timestamp,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            byte[] payload,
            string method,
            Uri url)
            : base(timestamp, headers, payload)
        {
            Method = method ?? string.Empty;
            Url = url;
        }

        /// <summary>HTTP method (GET, POST, ...).</summary>
        public string Method { get; }

        /// <summary>Target URL of the request.</summary>
        public Uri Url { get; }

        /// <summary>Host portion of <see cref="Url"/> (empty when unavailable).</summary>
        public string Host => Url is { IsAbsoluteUri: true } ? Url.Host : string.Empty;

        /// <summary>Path (and query) portion of <see cref="Url"/>.</summary>
        public string Path
        {
            get
            {
                if (Url is null)
                    return string.Empty;
                return Url.IsAbsoluteUri ? Url.PathAndQuery : Url.OriginalString;
            }
        }
    }

    public sealed class HttpResponse : HttpMessage
    {
        public HttpResponse(
            DateTimeOffset? timestamp,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            byte[] payload,
            int? statusCode = null,
            string? reasonPhrase = null)
            : base(timestamp, headers, payload)
        {
            StatusCode = statusCode;
            ReasonPhrase = reasonPhrase ?? string.Empty;
        }

        /// <summary>HTTP status code (e.g. 200), when parsed from the trace.</summary>
        public int? StatusCode { get; }

        /// <summary>Reason phrase associated with <see cref="StatusCode"/>.</summary>
        public string ReasonPhrase { get; }
    }
}
