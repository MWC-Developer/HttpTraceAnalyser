using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace HttpTraceAnalyser.Model
{
    /// <summary>
    /// Loads the JSON HTTP-export format produced by the Outlook tracing tool. The document is a
    /// JSON array whose items contain a request timestamp, method, URL, elapsed duration, raw
    /// request/response header blocks, and request/response body values with text or base64
    /// encoding markers.
    /// </summary>
    public sealed class JsonTraceFile : HttpTraceFile
    {
        public JsonTraceFile(string filePath) : base(filePath)
        {
            using var stream = File.OpenRead(filePath);
            using var document = JsonDocument.Parse(stream);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The Outlook JSON trace must contain an array of HTTP exchanges.");

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                var timestamp = TryGetDateTime(entry, "timestamp");
                var request = new HttpRequest(
                    timestamp,
                    ParseHeaders(GetString(entry, "requestHeaders")),
                    DecodeBody(GetString(entry, "requestBody"), GetString(entry, "requestBodyEncoding")),
                    GetString(entry, "method") ?? string.Empty,
                    ParseUrl(GetString(entry, "url")));

                var responseHeadersBlock = GetString(entry, "responseHeaders");
                var responseBody = GetString(entry, "responseBody");
                var responseBodyEncoding = GetString(entry, "responseBodyEncoding");
                var response = CreateResponse(entry, timestamp, responseHeadersBlock, responseBody, responseBodyEncoding);

                AddRow(request, response);
            }
        }

        private static HttpResponse? CreateResponse(
            JsonElement entry,
            DateTimeOffset? requestTimestamp,
            string? headerBlock,
            string? body,
            string? bodyEncoding)
        {
            if (string.IsNullOrEmpty(headerBlock) && string.IsNullOrEmpty(body))
                return null;

            var (statusCode, reasonPhrase, headers) = ParseResponseHeaders(headerBlock);
            var responseTimestamp = requestTimestamp;
            if (requestTimestamp.HasValue &&
                entry.TryGetProperty("elapsedMs", out var elapsed) &&
                elapsed.ValueKind == JsonValueKind.Number &&
                elapsed.TryGetDouble(out var elapsedMs) &&
                elapsedMs >= 0)
            {
                responseTimestamp = requestTimestamp.Value.AddMilliseconds(elapsedMs);
            }

            return new HttpResponse(
                responseTimestamp,
                headers,
                DecodeBody(body, bodyEncoding),
                statusCode,
                reasonPhrase);
        }

        private static Uri ParseUrl(string? urlText)
        {
            if (string.IsNullOrWhiteSpace(urlText))
                return new Uri("about:blank");

            if (Uri.TryCreate(urlText, UriKind.Absolute, out var absolute))
                return absolute;

            if (Uri.TryCreate(urlText, UriKind.RelativeOrAbsolute, out var relativeOrAbsolute))
                return relativeOrAbsolute;

            // Outlook exports may deliberately omit a host, yielding a syntactically invalid URI
            // such as "https:///mapi/...". Preserve the captured route as a relative URI instead
            // of discarding it entirely.
            var hostlessSchemeSeparator = urlText.IndexOf(":///", StringComparison.Ordinal);
            if (hostlessSchemeSeparator >= 0 &&
                Uri.TryCreate(urlText[(hostlessSchemeSeparator + 3)..], UriKind.Relative, out var hostlessPath))
            {
                return hostlessPath;
            }

            return new Uri("about:blank");
        }

        private static (int? StatusCode, string ReasonPhrase, List<KeyValuePair<string, string>> Headers) ParseResponseHeaders(string? headerBlock)
        {
            var lines = SplitLines(headerBlock);
            var statusCode = default(int?);
            var reasonPhrase = string.Empty;

            if (lines.Count > 0 && lines[0].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            {
                var parts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code))
                    statusCode = code;
                if (parts.Length == 3)
                    reasonPhrase = parts[2];
                lines.RemoveAt(0);
            }

            return (statusCode, reasonPhrase, ParseHeaderLines(lines));
        }

        private static List<KeyValuePair<string, string>> ParseHeaders(string? headerBlock)
            => ParseHeaderLines(SplitLines(headerBlock));

        private static List<KeyValuePair<string, string>> ParseHeaderLines(IEnumerable<string> lines)
        {
            var headers = new List<KeyValuePair<string, string>>();
            foreach (var line in lines)
            {
                var separator = line.IndexOf(':');
                if (separator <= 0)
                    continue;

                var name = line[..separator].Trim();
                if (name.Length == 0)
                    continue;

                headers.Add(new KeyValuePair<string, string>(name, line[(separator + 1)..].Trim()));
            }

            return headers;
        }

        private static List<string> SplitLines(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return new List<string>();

            return new List<string>(value.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static byte[] DecodeBody(string? body, string? encoding)
        {
            if (string.IsNullOrEmpty(body))
                return Array.Empty<byte>();

            if (string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return Convert.FromBase64String(body);
                }
                catch (FormatException)
                {
                    // Preserve invalidly labelled data rather than dropping captured content.
                }
            }

            return Encoding.UTF8.GetBytes(body);
        }

        private static string? GetString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
        }

        private static DateTimeOffset? TryGetDateTime(JsonElement element, string propertyName)
        {
            var text = GetString(element, propertyName);
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp)
                ? timestamp
                : null;
        }
    }
}
