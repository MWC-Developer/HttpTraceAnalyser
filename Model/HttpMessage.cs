using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

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
            : this(timestamp, headers, payload, decodePayload: true)
        {
        }

        protected HttpMessage(
            DateTimeOffset? timestamp,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            byte[] payload,
            bool decodePayload)
        {
            Timestamp = timestamp;
            Headers = headers ?? Array.Empty<KeyValuePair<string, string>>();
            var safePayload = payload ?? Array.Empty<byte>();
            Payload = decodePayload ? DecompressPayload(safePayload, Headers) : safePayload;
        }

        private static byte[] DecompressPayload(
            byte[] payload,
            IReadOnlyList<KeyValuePair<string, string>> headers)
        {
            if (payload.Length == 0)
                return payload;

            var encodings = GetContentEncodings(headers);
            var decoded = payload;

            try
            {
                // Content-Encoding lists codings in application order, so decode them in reverse.
                for (var index = encodings.Count - 1; index >= 0; index--)
                    decoded = Decompress(decoded, encodings[index]);

                return decoded;
            }
            catch (InvalidDataException)
            {
                return payload;
            }
        }

        private static byte[] Decompress(byte[] payload, string encoding)
        {
            using var input = new MemoryStream(payload, writable: false);
            using Stream decompressor = encoding switch
            {
                "gzip" => new GZipStream(input, CompressionMode.Decompress),
                "deflate" => new ZLibStream(input, CompressionMode.Decompress),
                "br" => new BrotliStream(input, CompressionMode.Decompress),
                _ => throw new InvalidDataException($"Unsupported content encoding: {encoding}")
            };
            using var output = new MemoryStream();
            decompressor.CopyTo(output);
            return output.ToArray();
        }

        private static IReadOnlyList<string> GetContentEncodings(
            IReadOnlyList<KeyValuePair<string, string>> headers)
        {
            var encodings = new List<string>();

            foreach (var header in headers)
            {
                if (!string.Equals(header.Key, "Content-Encoding", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var encoding in (header.Value ?? string.Empty).Split(','))
                {
                    var normalizedEncoding = encoding.Trim().ToLowerInvariant();
                    if (normalizedEncoding.Length > 0)
                        encodings.Add(normalizedEncoding);
                }
            }

            return encodings;
        }

        /// <summary>Time the message was captured, if known.</summary>
        public DateTimeOffset? Timestamp { get; }

        /// <summary>Headers in original order (duplicates preserved).</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

        /// <summary>Message body bytes, decompressed for supported Content-Encoding values.</summary>
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

        internal HttpRequest(
            DateTimeOffset? timestamp,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            byte[] payload,
            string method,
            Uri url,
            bool decodePayload)
            : base(timestamp, headers, payload, decodePayload)
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

        internal HttpResponse(
            DateTimeOffset? timestamp,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            byte[] payload,
            int? statusCode,
            string? reasonPhrase,
            bool decodePayload)
            : base(timestamp, headers, payload, decodePayload)
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
