using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace HttpTraceAnalyser.Model
{
    /// <summary>
    /// Loads an HTTP Archive (.har) file (HAR 1.2 JSON format).
    /// See https://w3c.github.io/web-performance/specs/HAR/Overview.html
    /// </summary>
    public sealed class HarTraceFile : HttpTraceFile
    {
        public HarTraceFile(string filePath) : base(filePath)
        {
            using var stream = File.OpenRead(filePath);
            using var doc = JsonDocument.Parse(stream);
            Load(doc.RootElement);
        }

        private void Load(JsonElement root)
        {
            if (!root.TryGetProperty("log", out var log) ||
                !log.TryGetProperty("entries", out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                var startedDateTime = TryGetDateTime(entry, "startedDateTime");
                var responseTime = startedDateTime;
                if (startedDateTime.HasValue &&
                    entry.TryGetProperty("time", out var timeProp) &&
                    timeProp.ValueKind == JsonValueKind.Number &&
                    timeProp.TryGetDouble(out var totalMs) &&
                    totalMs > 0)
                {
                    responseTime = startedDateTime.Value.AddMilliseconds(totalMs);
                }

                var request = entry.TryGetProperty("request", out var reqEl)
                    ? ParseRequest(reqEl, startedDateTime)
                    : null;
                if (request is null)
                    continue;

                HttpResponse? response = null;
                if (entry.TryGetProperty("response", out var respEl) && respEl.ValueKind == JsonValueKind.Object)
                {
                    response = ParseResponse(respEl, responseTime);
                }

                AddRow(request, response);
            }
        }

        private static HttpRequest ParseRequest(JsonElement el, DateTimeOffset? timestamp)
        {
            var method = GetString(el, "method") ?? string.Empty;
            var urlText = GetString(el, "url") ?? string.Empty;
            var headers = ParseHeaders(el);

            byte[] payload = Array.Empty<byte>();
            if (el.TryGetProperty("postData", out var postData) && postData.ValueKind == JsonValueKind.Object)
            {
                var text = GetString(postData, "text");
                if (!string.IsNullOrEmpty(text))
                    payload = Encoding.UTF8.GetBytes(text);
            }

            var url = Uri.TryCreate(urlText, UriKind.Absolute, out var abs)
                ? abs
                : new Uri(string.IsNullOrEmpty(urlText) ? "about:blank" : urlText, UriKind.RelativeOrAbsolute);

            return new HttpRequest(timestamp, headers, payload, method, url);
        }

        private static HttpResponse ParseResponse(JsonElement el, DateTimeOffset? timestamp)
        {
            int? statusCode = null;
            if (el.TryGetProperty("status", out var statusProp) &&
                statusProp.ValueKind == JsonValueKind.Number &&
                statusProp.TryGetInt32(out var code))
            {
                statusCode = code;
            }
            var reason = GetString(el, "statusText");
            var headers = ParseHeaders(el);

            byte[] payload = Array.Empty<byte>();
            if (el.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object)
            {
                var text = GetString(content, "text");
                if (!string.IsNullOrEmpty(text))
                {
                    var encoding = GetString(content, "encoding");
                    if (string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase))
                    {
                        try { payload = Convert.FromBase64String(text); }
                        catch { payload = Encoding.UTF8.GetBytes(text); }
                    }
                    else
                    {
                        payload = Encoding.UTF8.GetBytes(text);
                    }
                }
            }

            return new HttpResponse(timestamp, headers, payload, statusCode, reason);
        }

        private static List<KeyValuePair<string, string>> ParseHeaders(JsonElement el)
        {
            var result = new List<KeyValuePair<string, string>>();
            if (!el.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var h in headers.EnumerateArray())
            {
                var name = GetString(h, "name");
                if (string.IsNullOrEmpty(name))
                    continue;
                var value = GetString(h, "value") ?? string.Empty;
                result.Add(new KeyValuePair<string, string>(name, value));
            }
            return result;
        }

        private static string? GetString(JsonElement el, string name)
        {
            if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
            return null;
        }

        private static DateTimeOffset? TryGetDateTime(JsonElement el, string name)
        {
            var text = GetString(el, name);
            if (string.IsNullOrWhiteSpace(text))
                return null;
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            {
                return dto;
            }
            return null;
        }
    }
}
